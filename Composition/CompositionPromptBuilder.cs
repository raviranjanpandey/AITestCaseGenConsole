using System.Text;
using System.Text.Json;
using TestAIPoc.Analysis;
using TestAIPoc.Models;

namespace TestAIPoc.Composition;

internal static class CompositionPromptBuilder
{
    public static string BuildPrompt(
        PipelineRun run,
        ContextDocument context,
        RepositoryAnalysis analysis,
        string? repairPrompt)
    {
        // Slim file index: path, score, and evidence IDs — used for traceability references.
        var evidenceSummary = string.Join(Environment.NewLine, analysis.SelectedFiles.Select(file =>
        {
            var evidenceIds = string.Join(", ", file.Evidence.Select(e => e.EvidenceId).Take(3));
            return $"- {file.RelativePath} | score={file.Score:F1} | evidence={evidenceIds}";
        }));

        var builder = new StringBuilder();
        builder.AppendLine($"Target module: {run.ModuleName}");
        builder.AppendLine($"User prompt: {run.Prompt}");

        if (analysis.ClientWorkspace is not null)
        {
            builder.AppendLine($"Server commit: {analysis.Workspace.CommitSha}");
            builder.AppendLine($"Client commit: {analysis.ClientWorkspace.CommitSha}");
            builder.AppendLine("Stack: .NET Web API (server) + React (client)");
        }
        else
        {
            builder.AppendLine($"Commit: {context.Repository.CommitSha}");
        }

        var stackType = LanguageDetector.GetStackType(analysis.DetectedLanguages);
        builder.AppendLine($"Application type: {stackType}");
        builder.AppendLine($"Technologies: {string.Join(", ", analysis.DetectedLanguages)}");
        if (analysis.ClientWorkspace is not null)
            builder.AppendLine("Note: this context was synthesized from both server-side and client-side codebases.");

        builder.AppendLine();
        builder.AppendLine("Use this structured context as the only source of truth:");
        builder.AppendLine(SlimContextJson(context));
        builder.AppendLine();
        builder.AppendLine("Evidence files (path | relevance score | evidence IDs for traceability):");
        builder.AppendLine(evidenceSummary);
        builder.AppendLine();
        builder.AppendLine("Generate 6 to 10 test cases covering:");
        builder.AppendLine("- happy path");
        builder.AppendLine("- validation failures");
        builder.AppendLine("- negative scenarios");
        builder.AppendLine("- edge cases and boundary values");
        builder.AppendLine("- authorization or permission checks if relevant");
        builder.AppendLine();
        builder.AppendLine("Every testcase must include: title, category, severity, test_type, automation_suitability, priority, risk, preconditions, steps, expected_results, traceability, notes.");
        builder.AppendLine("In traceability, reference statement IDs (e.g. BUSINESS-RULE-1) or evidence IDs (e.g. EV-abc123) from the context above.");
        builder.AppendLine("Keep the output concise, specific, and test-executable.");

        if (!string.IsNullOrWhiteSpace(repairPrompt))
        {
            builder.AppendLine();
            builder.AppendLine("REPAIR MODE:");
            builder.AppendLine("The previous response was invalid or incomplete.");
            builder.AppendLine("Return a corrected JSON object only, matching the schema exactly.");
            builder.AppendLine("Do not explain the repair.");
            builder.AppendLine("Previous response:");
            builder.AppendLine(repairPrompt);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns a compact JSON view of the context — drops EvidenceFiles (already in the evidence
    /// summary block) and all repository/run metadata irrelevant to the LLM.
    /// </summary>
    private static string SlimContextJson(ContextDocument ctx)
    {
        var slim = new
        {
            module        = ctx.ModuleName,
            summary       = ctx.Summary,
            businessRules = ctx.BusinessRules.Select(s => new { s.Id, s.Text, s.Confidence, s.EvidenceIds }),
            validations   = ctx.Validations.Select(s   => new { s.Id, s.Text, s.Confidence, s.EvidenceIds }),
            flows         = ctx.Flows.Select(s         => new { s.Id, s.Text, s.Confidence }),
            dependencies  = ctx.Dependencies.Select(s  => new { s.Id, s.Text, s.Confidence }),
            edgeCases     = ctx.EdgeCases.Select(s     => new { s.Id, s.Text, s.Confidence }),
            assumptions   = ctx.Assumptions.Select(s   => new { s.Id, s.Text }),
            openQuestions = ctx.OpenQuestions.Select(s => new { s.Id, s.Text })
        };
        return JsonSerializer.Serialize(slim, JsonOptions.Pretty);
    }

    public static string BuildRepairPrompt(ContextDocument context, string rawJson, string reason)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Repair reason: {reason}");
        builder.AppendLine("Return a single corrected JSON object only.");
        builder.AppendLine("Keep the shape identical to the schema.");
        builder.AppendLine("Do not add commentary.");
        builder.AppendLine();
        builder.AppendLine("Context summary:");
        builder.AppendLine(context.Summary);
        builder.AppendLine();
        builder.AppendLine("Previous invalid or partial output:");
        builder.AppendLine(rawJson);
        return builder.ToString();
    }
}
