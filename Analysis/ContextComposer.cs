using System.Text;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Analysis;

public sealed class ContextComposer
{
    public ContextDocument Compose(PipelineRun run, RepositoryWorkspace workspace, RepositoryAnalysis analysis)
    {
        var evidenceMap = analysis.SelectedFiles
            .SelectMany(file => file.Evidence)
            .ToDictionary(evidence => evidence.EvidenceId, evidence => evidence, StringComparer.OrdinalIgnoreCase);

        var rules = CollectStatements("business-rule", analysis.SelectedFiles, evidenceMap, [
            "must", "should", "required", "cannot", "reject", "allow", "only", "unless", "must not"
        ]);

        var validations = CollectStatements("validation", analysis.SelectedFiles, evidenceMap, [
            "validate", "validator", "required", "badrequest", "modelstate", "range", "length", "format", "authorize"
        ]);

        var flows = BuildFlows(analysis.SelectedFiles, evidenceMap);
        var dependencies = BuildDependencies(analysis.SelectedFiles, evidenceMap);
        var edgeCases = BuildEdgeCases(analysis.SelectedFiles, evidenceMap);
        var assumptions = BuildAssumptions(run, analysis);
        var questions = BuildOpenQuestions(run, analysis, rules, validations);

        var summary = BuildSummary(run, workspace, analysis, rules, validations);

        return new ContextDocument
        {
            RunId = run.RunId,
            Prompt = run.Prompt,
            ModuleName = run.ModuleName,
            GeneratedUtc = DateTimeOffset.UtcNow,
            Repository = workspace,
            Summary = summary,
            BusinessRules = rules,
            Validations = validations,
            Flows = flows,
            Dependencies = dependencies,
            EdgeCases = edgeCases,
            Assumptions = assumptions,
            OpenQuestions = questions,
            EvidenceFiles = analysis.SelectedFiles
        };
    }

    public string RenderMarkdown(ContextDocument context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Context for {context.ModuleName}");
        builder.AppendLine();
        builder.AppendLine($"- Run: `{context.RunId}`");
        builder.AppendLine($"- Repository: `{context.Repository.RepositoryPath}`");
        builder.AppendLine($"- Commit: `{context.Repository.CommitSha}`");
        builder.AppendLine($"- Prompt: {context.Prompt}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine(context.Summary);
        builder.AppendLine();

        AppendSection(builder, "Business Rules", context.BusinessRules);
        AppendSection(builder, "Validations", context.Validations);
        AppendSection(builder, "Flows", context.Flows);
        AppendSection(builder, "Dependencies", context.Dependencies);
        AppendSection(builder, "Edge Cases", context.EdgeCases);
        AppendSection(builder, "Assumptions", context.Assumptions);
        AppendSection(builder, "Open Questions", context.OpenQuestions);

        builder.AppendLine("## Evidence Files");
        foreach (var file in context.EvidenceFiles)
        {
            builder.AppendLine($"- `{file.RelativePath}` ({file.Language}, score {file.Score:F1})");
            foreach (var evidence in file.Evidence)
            {
                builder.AppendLine($"  - {evidence.Note} lines {evidence.LineStart}-{evidence.LineEnd}");
            }
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyList<ContextStatement> statements)
    {
        builder.AppendLine($"## {title}");
        if (statements.Count == 0)
        {
            builder.AppendLine("- None detected");
            builder.AppendLine();
            return;
        }

        foreach (var statement in statements)
        {
            builder.AppendLine($"- [{statement.Id}] {statement.Text}");
            if (statement.EvidenceIds.Count > 0)
            {
                builder.AppendLine($"  - Evidence: {string.Join(", ", statement.EvidenceIds)}");
            }
        }

        builder.AppendLine();
    }

    private static List<ContextStatement> CollectStatements(string prefix, IReadOnlyList<FileInsight> files, IDictionary<string, EvidenceSnippet> evidenceMap, IReadOnlyList<string> keywords)
    {
        var results = new List<ContextStatement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            foreach (var evidence in file.Evidence)
            {
                var text = evidence.Snippet;
                if (!keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var normalized = NormalizeStatement(text);
                if (normalized.Length < 12 || !seen.Add(normalized))
                {
                    continue;
                }

                results.Add(new ContextStatement
                {
                    Id = $"{prefix.ToUpperInvariant()}-{results.Count + 1}",
                    Category = prefix,
                    Text = normalized,
                    EvidenceIds = [evidence.EvidenceId],
                    Confidence = file.Score >= 8 ? "high" : file.Score >= 4 ? "medium" : "low"
                });

                if (results.Count >= 8)
                {
                    return results;
                }
            }
        }

        if (results.Count == 0 && files.Count > 0)
        {
            var fallback = files[0];
            results.Add(new ContextStatement
            {
                Id = $"{prefix.ToUpperInvariant()}-1",
                Category = prefix,
                Text = $"No explicit {prefix.Replace('-', ' ')} text was found; inspect `{fallback.RelativePath}` first.",
                EvidenceIds = fallback.Evidence.Select(e => e.EvidenceId).Take(1).ToList(),
                Confidence = "low"
            });
        }

        return results;
    }

    private static List<ContextStatement> BuildFlows(IReadOnlyList<FileInsight> files, IDictionary<string, EvidenceSnippet> evidenceMap)
    {
        var results = new List<ContextStatement>();
        var controllerFile = files.FirstOrDefault(file => file.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase));
        if (controllerFile is not null)
        {
            results.Add(new ContextStatement
            {
                Id = "FLOW-1",
                Category = "flow",
                Text = $"User entry point appears to be `{controllerFile.RelativePath}`; route/action handling should flow through service and validation layers before persistence.",
                EvidenceIds = controllerFile.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                Confidence = controllerFile.Score >= 8 ? "high" : "medium"
            });
        }

        var serviceFile = files.FirstOrDefault(file => file.RelativePath.Contains("service", StringComparison.OrdinalIgnoreCase));
        if (serviceFile is not null)
        {
            results.Add(new ContextStatement
            {
                Id = "FLOW-2",
                Category = "flow",
                Text = $"Service logic is likely centralized in `{serviceFile.RelativePath}`; capture the request-to-domain transformation there.",
                EvidenceIds = serviceFile.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                Confidence = serviceFile.Score >= 8 ? "high" : "medium"
            });
        }

        if (results.Count == 0 && files.Count > 0)
        {
            results.Add(new ContextStatement
            {
                Id = "FLOW-1",
                Category = "flow",
                Text = $"No obvious controller/service split found. Inspect `{files[0].RelativePath}` as the lead implementation file.",
                EvidenceIds = files[0].Evidence.Select(e => e.EvidenceId).Take(1).ToList(),
                Confidence = "low"
            });
        }

        return results;
    }

    private static List<ContextStatement> BuildDependencies(IReadOnlyList<FileInsight> files, IDictionary<string, EvidenceSnippet> evidenceMap)
    {
        var dependencies = new List<ContextStatement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            foreach (var usingName in file.Usings)
            {
                if (!seen.Add(usingName))
                {
                    continue;
                }

                dependencies.Add(new ContextStatement
                {
                    Id = $"DEP-{dependencies.Count + 1}",
                    Category = "dependency",
                    Text = usingName,
                    EvidenceIds = file.Evidence.Select(e => e.EvidenceId).Take(1).ToList(),
                    Confidence = "medium"
                });

                if (dependencies.Count >= 8)
                {
                    return dependencies;
                }
            }
        }

        if (dependencies.Count == 0 && files.Count > 0)
        {
            dependencies.Add(new ContextStatement
            {
                Id = "DEP-1",
                Category = "dependency",
                Text = $"Dependencies were not explicit in the scanned slice; use `{files[0].RelativePath}` as the anchor file for manual follow-up.",
                EvidenceIds = files[0].Evidence.Select(e => e.EvidenceId).Take(1).ToList(),
                Confidence = "low"
            });
        }

        return dependencies;
    }

    private static List<ContextStatement> BuildEdgeCases(IReadOnlyList<FileInsight> files, IDictionary<string, EvidenceSnippet> evidenceMap)
    {
        var results = new List<ContextStatement>();
        foreach (var file in files)
        {
            foreach (var evidence in file.Evidence)
            {
                var text = evidence.Snippet;
                if (!(text.Contains("null", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("empty", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("already", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                results.Add(new ContextStatement
                {
                    Id = $"EDGE-{results.Count + 1}",
                    Category = "edge-case",
                    Text = NormalizeStatement(text),
                    EvidenceIds = [evidence.EvidenceId],
                    Confidence = file.Score >= 8 ? "high" : "medium"
                });

                if (results.Count >= 6)
                {
                    return results;
                }
            }
        }

        if (results.Count == 0 && files.Count > 0)
        {
            results.Add(new ContextStatement
            {
                Id = "EDGE-1",
                Category = "edge-case",
                Text = "No explicit edge cases were detected; generate validation coverage for missing, null, empty, duplicate, and boundary values.",
                EvidenceIds = files[0].Evidence.Select(e => e.EvidenceId).Take(1).ToList(),
                Confidence = "low"
            });
        }

        return results;
    }

    private static List<ContextStatement> BuildAssumptions(PipelineRun run, RepositoryAnalysis analysis)
    {
        var assumptions = new List<ContextStatement>
        {
            new()
            {
                Id = "ASSUMP-1",
                Category = "assumption",
                Text = $"The prompt `{run.Prompt}` is treated as the source of truth for the target module and test scope.",
                EvidenceIds = [],
                Confidence = "high"
            }
        };

        if (!analysis.SelectedFiles.Any(file => file.RelativePath.Contains("test", StringComparison.OrdinalIgnoreCase)))
        {
            assumptions.Add(new ContextStatement
            {
                Id = "ASSUMP-2",
                Category = "assumption",
                Text = "The repository slice does not appear to include dedicated tests, so the generated test set should be treated as a new baseline.",
                EvidenceIds = [],
                Confidence = "medium"
            });
        }

        return assumptions;
    }

    private static List<ContextStatement> BuildOpenQuestions(PipelineRun run, RepositoryAnalysis analysis, IReadOnlyList<ContextStatement> rules, IReadOnlyList<ContextStatement> validations)
    {
        var questions = new List<ContextStatement>();

        if (!analysis.SelectedFiles.Any(file => file.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase)))
        {
            questions.Add(new ContextStatement
            {
                Id = "Q-1",
                Category = "open-question",
                Text = "Which file should be treated as the canonical entry point for this module?",
                EvidenceIds = [],
                Confidence = "low"
            });
        }

        if (validations.Count == 0)
        {
            questions.Add(new ContextStatement
            {
                Id = "Q-2",
                Category = "open-question",
                Text = "Are there external validation or authorization rules that live outside the scanned repo slice?",
                EvidenceIds = [],
                Confidence = "low"
            });
        }

        if (rules.Count == 0)
        {
            questions.Add(new ContextStatement
            {
                Id = "Q-3",
                Category = "open-question",
                Text = $"The prompt `{run.Prompt}` did not yield explicit business rules. Is there a specific leave type, approval state, or actor model that should be preferred?",
                EvidenceIds = [],
                Confidence = "low"
            });
        }

        return questions;
    }

    private static string BuildSummary(PipelineRun run, RepositoryWorkspace workspace, RepositoryAnalysis analysis, IReadOnlyList<ContextStatement> rules, IReadOnlyList<ContextStatement> validations)
    {
        var topFiles = string.Join(", ", analysis.SelectedFiles.Take(4).Select(file => $"`{file.RelativePath}`"));
        return
            $"The prompt targets `{run.ModuleName}` inside repository commit `{workspace.CommitSha}`. " +
            $"The analysis prioritized {analysis.SelectedFiles.Count} files, led by {topFiles}. " +
            $"It surfaced {rules.Count} business rules and {validations.Count} validation signals that should anchor test generation.";
    }

    private static string NormalizeStatement(string text)
    {
        var flattened = string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        flattened = flattened.Replace("\t", " ").Trim();
        if (flattened.Length > 240)
        {
            flattened = flattened[..237] + "...";
        }

        return flattened;
    }
}

