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
            "must", "should", "required", "cannot", "reject", "allow", "only", "unless", "must not",
            "forbidden", "unauthorized", "permission", "policy", "constraint", "minimum", "maximum"
        ]);

        var validations = CollectStatements("validation", analysis.SelectedFiles, evidenceMap, [
            "validate", "validator", "required", "badrequest", "modelstate", "range", "length", "format", "authorize",
            "yup", "zod", "joi", "schema", "throw", "reject", "@valid", "serializer", "raise",
            "pattern", "minlength", "maxlength", "validation_error", "validationerror"
        ]);

        var flows = BuildFlows(analysis.SelectedFiles, evidenceMap, analysis.PrimaryLanguage,
            analysis.DetectedLanguages, analysis.ClientWorkspace is not null);
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

    private static List<ContextStatement> BuildFlows(IReadOnlyList<FileInsight> files,
        IDictionary<string, EvidenceSnippet> evidenceMap, string primaryLanguage,
        IReadOnlyList<string> detectedLanguages, bool isDualRepo)
    {
        var results = new List<ContextStatement>();
        var isFullStack = isDualRepo || LanguageDetector.IsFullStack(detectedLanguages);

        if (isFullStack)
        {
            // Server-side API entry point — prefer files tagged "server", fall back to language heuristic
            var serverFiles = files.Where(f => f.Layer == "server").ToList();
            var clientFiles = files.Where(f => f.Layer == "client").ToList();
            var untagged    = files.Where(f => f.Layer is null).ToList();

            var apiPool = serverFiles.Count > 0 ? serverFiles : untagged;
            var uiPool  = clientFiles.Count > 0 ? clientFiles : untagged;

            var apiEntry =
                apiPool.FirstOrDefault(f => f.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase)) ??
                apiPool.FirstOrDefault(f => f.Language is "CSharp" or "Java" or "Kotlin" or "Python");

            if (apiEntry is not null)
            {
                results.Add(new ContextStatement
                {
                    Id = "FLOW-1",
                    Category = "flow",
                    Text = $"[Server] API entry point: `{apiEntry.RelativePath}`. HTTP requests arrive here, pass through service and validation layers, then persist to the database. Test HTTP contracts, status codes, request validation, business rules, and authorization.",
                    EvidenceIds = apiEntry.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                    Confidence = apiEntry.Score >= 8 ? "high" : "medium"
                });
            }

            var uiEntry =
                uiPool.FirstOrDefault(f =>
                    f.RelativePath.Contains("/pages/", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("/routes/", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("App.tsx", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("App.jsx", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("component", StringComparison.OrdinalIgnoreCase)) ??
                uiPool.FirstOrDefault(f => f.Language is "TypeScript" or "JavaScript");

            if (uiEntry is not null)
            {
                results.Add(new ContextStatement
                {
                    Id = "FLOW-2",
                    Category = "flow",
                    Text = $"[Client] React entry point: `{uiEntry.RelativePath}`. Component drives user interactions, calls the API service layer, and reflects state changes. Test rendering, form validation, API success/failure handling, loading states, and navigation.",
                    EvidenceIds = uiEntry.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                    Confidence = uiEntry.Score >= 8 ? "high" : "medium"
                });
            }

            var bridgeFile =
                uiPool.FirstOrDefault(f =>
                    f.RelativePath.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("/api/", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("hook", StringComparison.OrdinalIgnoreCase) ||
                    f.RelativePath.Contains("slice", StringComparison.OrdinalIgnoreCase));

            if (bridgeFile is not null)
            {
                results.Add(new ContextStatement
                {
                    Id = "FLOW-3",
                    Category = "flow",
                    Text = $"[Client→Server bridge] `{bridgeFile.RelativePath}` connects the React UI to the .NET API. Test request formation, response deserialization, loading/error states, and network failure handling.",
                    EvidenceIds = bridgeFile.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                    Confidence = bridgeFile.Score >= 6 ? "medium" : "low"
                });
            }
        }
        else
        {
            // Single-stack flow detection
            var entryFile = primaryLanguage switch
            {
                "TypeScript" or "JavaScript" =>
                    files.FirstOrDefault(f =>
                        f.RelativePath.Contains("App.tsx", StringComparison.OrdinalIgnoreCase) ||
                        f.RelativePath.Contains("App.jsx", StringComparison.OrdinalIgnoreCase) ||
                        f.RelativePath.Contains("router", StringComparison.OrdinalIgnoreCase) ||
                        f.RelativePath.Contains("/pages/", StringComparison.OrdinalIgnoreCase) ||
                        f.RelativePath.Contains("/routes/", StringComparison.OrdinalIgnoreCase)) ??
                    files.FirstOrDefault(f => f.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase)),
                "Python" =>
                    files.FirstOrDefault(f =>
                        f.RelativePath.Contains("views.py", StringComparison.OrdinalIgnoreCase) ||
                        f.RelativePath.Contains("urls.py", StringComparison.OrdinalIgnoreCase)),
                "Java" or "Kotlin" =>
                    files.FirstOrDefault(f => f.RelativePath.Contains("Controller", StringComparison.OrdinalIgnoreCase)),
                _ =>
                    files.FirstOrDefault(f => f.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase))
            };
            entryFile ??= files.FirstOrDefault();

            if (entryFile is not null)
            {
                var entryKind = primaryLanguage switch
                {
                    "TypeScript" or "JavaScript" => "component / page / router",
                    "Python"                     => "view / URL handler",
                    "Java" or "Kotlin"           => "controller / REST endpoint",
                    _                            => "controller / entry point"
                };
                results.Add(new ContextStatement
                {
                    Id = "FLOW-1",
                    Category = "flow",
                    Text = $"Entry point: `{entryFile.RelativePath}` ({entryKind}). Flow should pass through service and validation layers before any state change or persistence.",
                    EvidenceIds = entryFile.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                    Confidence = entryFile.Score >= 8 ? "high" : "medium"
                });
            }

            var servicePatterns = primaryLanguage switch
            {
                "TypeScript" or "JavaScript" => new[] { "service", "/api/", "store", "hook", "context", "slice" },
                "Python"                     => new[] { "service", "serializer", "repository" },
                _                            => new[] { "service", "repository", "manager" }
            };
            var serviceFile = files.FirstOrDefault(f =>
                servicePatterns.Any(p => f.RelativePath.Contains(p, StringComparison.OrdinalIgnoreCase)));

            if (serviceFile is not null)
            {
                results.Add(new ContextStatement
                {
                    Id = "FLOW-2",
                    Category = "flow",
                    Text = $"Service / data logic: `{serviceFile.RelativePath}`. Capture request-to-state transformation and side-effects here.",
                    EvidenceIds = serviceFile.Evidence.Select(e => e.EvidenceId).Take(2).ToList(),
                    Confidence = serviceFile.Score >= 8 ? "high" : "medium"
                });
            }
        }

        if (results.Count == 0 && files.Count > 0)
        {
            results.Add(new ContextStatement
            {
                Id = "FLOW-1",
                Category = "flow",
                Text = $"No obvious entry point found. Inspect `{files[0].RelativePath}` as the lead implementation file.",
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

        var hasEntryPoint = analysis.SelectedFiles.Any(f =>
            f.RelativePath.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("component", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("/page", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("view", StringComparison.OrdinalIgnoreCase) ||
            f.RelativePath.Contains("router", StringComparison.OrdinalIgnoreCase));
        if (!hasEntryPoint)
        {
            questions.Add(new ContextStatement
            {
                Id = "Q-1",
                Category = "open-question",
                Text = "Which file should be treated as the canonical entry point for this module (controller, page, view, or router)?",
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
        var topFiles = string.Join(", ", analysis.SelectedFiles.Take(4).Select(file =>
        {
            var label = file.Layer is not null ? $"[{file.Layer}] " : string.Empty;
            return $"`{label}{file.RelativePath}`";
        }));

        var repoDescription = analysis.ClientWorkspace is not null
            ? $"server commit `{workspace.CommitSha}` + client commit `{analysis.ClientWorkspace.CommitSha}`"
            : $"commit `{workspace.CommitSha}`";

        return
            $"The prompt targets `{run.ModuleName}` ({repoDescription}). " +
            $"The analysis selected {analysis.SelectedFiles.Count} files across " +
            (analysis.ClientWorkspace is not null ? "server and client repositories" : "the repository") +
            $", led by {topFiles}. " +
            $"It surfaced {rules.Count} business rule(s) and {validations.Count} validation signal(s) that anchor test generation.";
    }

    public ContextDocument MergeContexts(
        ContextDocument server,
        ContextDocument client,
        PipelineRun run,
        RepositoryWorkspace serverWorkspace)
    {
        var mergedSummary =
            $"Merged context for `{run.ModuleName}` synthesized from both server-side and client-side codebases.\n\n" +
            $"Server-side: {server.Summary}\n\n" +
            $"Client-side: {client.Summary}";

        return new ContextDocument
        {
            RunId = run.RunId,
            Prompt = run.Prompt,
            ModuleName = run.ModuleName,
            GeneratedUtc = DateTimeOffset.UtcNow,
            Repository = serverWorkspace,
            Summary = mergedSummary,
            BusinessRules = MergeStatements(server.BusinessRules, client.BusinessRules, "BUSINESS-RULE"),
            Validations   = MergeStatements(server.Validations,   client.Validations,   "VALIDATION"),
            Flows         = MergeFlows(server.Flows, client.Flows),
            Dependencies  = MergeStatements(server.Dependencies,  client.Dependencies,  "DEP"),
            EdgeCases     = MergeStatements(server.EdgeCases,     client.EdgeCases,     "EDGE"),
            Assumptions   = MergeStatements(server.Assumptions,   client.Assumptions,   "ASSUMP"),
            OpenQuestions = MergeStatements(server.OpenQuestions, client.OpenQuestions, "Q"),
            EvidenceFiles = server.EvidenceFiles.Concat(client.EvidenceFiles).ToList()
        };
    }

    private static IReadOnlyList<ContextStatement> MergeStatements(
        IReadOnlyList<ContextStatement> primary,
        IReadOnlyList<ContextStatement> secondary,
        string prefix)
    {
        var result = new List<ContextStatement>(primary);
        var seenText = new HashSet<string>(
            primary.Select(s => DedupKey(s.Text)),
            StringComparer.OrdinalIgnoreCase);
        var counter = primary.Count + 1;

        foreach (var stmt in secondary)
        {
            if (seenText.Add(DedupKey(stmt.Text)))
                result.Add(stmt with { Id = $"{prefix}-{counter++}" });
        }

        return result;
    }

    private static IReadOnlyList<ContextStatement> MergeFlows(
        IReadOnlyList<ContextStatement> serverFlows,
        IReadOnlyList<ContextStatement> clientFlows)
    {
        var result = new List<ContextStatement>();
        var counter = 1;
        foreach (var flow in serverFlows.Concat(clientFlows))
            result.Add(flow with { Id = $"FLOW-{counter++}" });
        return result;
    }

    private static string DedupKey(string text) =>
        text.Length > 80 ? text[..80].ToLowerInvariant().Trim() : text.ToLowerInvariant().Trim();

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

