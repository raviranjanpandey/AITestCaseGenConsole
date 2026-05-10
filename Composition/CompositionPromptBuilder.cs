using System.Text;
using TestAIPoc.Analysis;
using TestAIPoc.Models;

namespace TestAIPoc.Composition;

internal static class CompositionPromptBuilder
{
    public static string BuildPrompt(
        PipelineRun run,
        string contextJson,
        RepositoryAnalysis analysis,
        string? repairPrompt)
    {
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
            builder.AppendLine($"Server-side repository: {analysis.Workspace.RepositoryPath} (commit: {analysis.Workspace.CommitSha})");
            builder.AppendLine($"Client-side repository: {analysis.ClientWorkspace.RepositoryPath} (commit: {analysis.ClientWorkspace.CommitSha})");
            builder.AppendLine("Stack: .NET Web API (server) + React (client)");
        }
        else
        {
            builder.AppendLine($"Repository: {run.RepositorySource}");
        }

        var stackType = LanguageDetector.GetStackType(analysis.DetectedLanguages);
        builder.AppendLine($"Application type: {stackType}");
        builder.AppendLine($"Technologies: {string.Join(", ", analysis.DetectedLanguages)}");
        if (analysis.ClientWorkspace is not null)
        {
            builder.AppendLine("Note: this context was synthesized from both server-side and client-side codebases and merged into a single unified view.");
        }

        builder.AppendLine();
        builder.AppendLine("Use this structured context as the only source of truth:");
        builder.AppendLine(contextJson);
        builder.AppendLine();
        builder.AppendLine("Important evidence files:");
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
        builder.AppendLine("In traceability, reuse only context statement IDs or evidence IDs that appear in the provided context.");
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
