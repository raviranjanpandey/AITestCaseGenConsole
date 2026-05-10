using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using OpenAI.Responses;
using TestAIPoc.Models;

namespace TestAIPoc.Analysis;

/// <summary>
/// Replaces the heuristic ContextComposer when --ai-context is set.
/// Sends the actual source file contents to an LLM and asks it to extract
/// business rules, validations, flows, dependencies, edge cases, assumptions,
/// and open questions, producing a richer ContextDocument for test generation.
/// Falls back to the heuristic ContextComposer on any failure.
/// </summary>
public sealed class AiContextComposer
{
    private const int MaxFilesForContext = 10;
    private const int FullContentFileCount = 5;  // top N files by score get full source
    private const int MaxFileSizeChars = 6_000;
    private const int MaxAttempts = 2;

    private readonly string _provider;
    private readonly string _modelName;
    private readonly string? _apiKey;
    private readonly ContextComposer _fallback;

    public AiContextComposer(string provider, string? modelName, string? apiKey, ContextComposer fallback)
    {
        _provider = provider.Trim().ToLowerInvariant();
        _apiKey = apiKey;
        _fallback = fallback;
        _modelName = string.IsNullOrWhiteSpace(modelName)
            ? _provider is "claude" or "anthropic" ? "claude-sonnet-4-6" : "gpt-4.1-mini"
            : modelName;
    }

    public string ModelName => _modelName;

    public async Task<ContextDocument> ComposeAsync(
        PipelineRun run,
        RepositoryWorkspace workspace,
        RepositoryAnalysis analysis)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Console.Error.WriteLine("[warn] No API key for AI context composer. Falling back to heuristic context.");
            return _fallback.Compose(run, workspace, analysis);
        }

        try
        {
            var fileContents = ReadFileContents(analysis);
            if (fileContents.Count == 0)
            {
                Console.Error.WriteLine("[warn] AI context composer: no readable files. Falling back to heuristic context.");
                return _fallback.Compose(run, workspace, analysis);
            }

            var prompt = BuildContextPrompt(run, analysis, fileContents);
            AiGeneratedContext? aiContext = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                Console.WriteLine($"  - AI context attempt {attempt}/{MaxAttempts}: analysing {fileContents.Count} file(s) with {_modelName}...");
                try
                {
                    var raw = _provider is "claude" or "anthropic"
                        ? await CallAnthropicAsync(prompt)
                        : await CallOpenAiAsync(prompt);

                    aiContext = JsonSerializer.Deserialize<AiGeneratedContext>(
                        StripJsonFences(raw), JsonOptions.Strict);

                    if (aiContext is not null) break;
                }
                catch (Exception ex) when (attempt < MaxAttempts)
                {
                    Console.Error.WriteLine($"[warn] AI context attempt {attempt} failed: {ex.Message}. Retrying...");
                    await Task.Delay(TimeSpan.FromMilliseconds(800));
                }
            }

            if (aiContext is null)
            {
                Console.Error.WriteLine("[warn] AI context generation failed after retries. Falling back to heuristic context.");
                return _fallback.Compose(run, workspace, analysis);
            }

            Console.WriteLine($"  - AI context extracted: {aiContext.BusinessRules.Count} rule(s), {aiContext.Validations.Count} validation(s), {aiContext.Flows.Count} flow(s).");
            return MapToContextDocument(aiContext, run, workspace, analysis);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] AI context composer error: {ex.Message}. Falling back to heuristic context.");
            return _fallback.Compose(run, workspace, analysis);
        }
    }

    private static List<(string RelativePath, string Content)> ReadFileContents(RepositoryAnalysis analysis)
    {
        var results = new List<(string, string)>();
        var files = analysis.SelectedFiles.Take(MaxFilesForContext).ToList();

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];

            // Top files: full source gives the AI the most signal.
            // Lower-ranked files: evidence snippets already extracted by the analyzer are enough.
            if (i < FullContentFileCount)
            {
                var full = TryReadFullContent(file.Path);
                if (full is not null)
                {
                    results.Add((file.RelativePath, full));
                    continue;
                }
            }

            // Fallback / lower-ranked files: use pre-extracted 3-line evidence windows.
            if (file.Evidence.Count > 0)
            {
                var snippets = string.Join(
                    "\n...\n",
                    file.Evidence.Select(e => $"// lines {e.LineStart}-{e.LineEnd}\n{e.Snippet}"));
                results.Add((file.RelativePath, snippets));
            }
        }

        return results;
    }

    private static string? TryReadFullContent(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            return content.Length > MaxFileSizeChars
                ? content[..MaxFileSizeChars] + "\n... [truncated for token budget]"
                : content;
        }
        catch { return null; }
    }

    private static string BuildContextPrompt(
        PipelineRun run,
        RepositoryAnalysis analysis,
        List<(string RelativePath, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Feature prompt: {run.Prompt}");
        sb.AppendLine($"Module: {run.ModuleName}");
        sb.AppendLine($"Technologies: {string.Join(", ", analysis.DetectedLanguages)}");
        sb.AppendLine();
        sb.AppendLine($"Source files to analyse (top {FullContentFileCount} shown in full; remainder are relevant excerpts):");
        sb.AppendLine();

        for (var i = 0; i < files.Count; i++)
        {
            var (path, content) = files[i];
            var label = i < FullContentFileCount ? "full source" : "excerpts";
            sb.AppendLine($"=== {path} [{label}] ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        sb.AppendLine("""
            Based on your analysis of the code above, return a JSON object with this exact structure:
            {
              "summary": "Concise description of what this feature does and how it works",
              "business_rules": [{ "text": "Specific rule derived from code logic", "confidence": "high|medium|low" }],
              "validations": [{ "text": "Specific validation found in the code (field constraints, auth checks, error handling)", "confidence": "high|medium|low" }],
              "flows": [{ "text": "Step-by-step description of the actual execution path through the code", "confidence": "high|medium|low" }],
              "dependencies": [{ "text": "External service, repository, or system the code depends on", "confidence": "high|medium|low" }],
              "edge_cases": [{ "text": "Specific edge case visible in the code (null checks, duplicate guards, boundary limits)", "confidence": "high|medium|low" }],
              "assumptions": [{ "text": "Assumption implicit in the code design", "confidence": "medium|low" }],
              "open_questions": [{ "text": "Ambiguity or missing information that affects testability", "confidence": "low" }]
            }

            Rules:
            - Ground every item in the actual code — no generic or template statements
            - Prefer concrete values (e.g. "maximum 30 days" not "there is a limit")
            - Each list may have 3–8 items; omit a category only if truly empty in the code
            - Return ONLY valid JSON — no markdown fences, no explanation
            """);

        return sb.ToString();
    }

    private async Task<string> CallAnthropicAsync(string prompt)
    {
        var client = new AnthropicClient(new APIAuthentication(_apiKey!));

        const string system = """
            You are a senior software analyst. Read the provided source code carefully and extract a precise,
            factual understanding of the feature. Ground every observation in what the code literally does.
            Return only valid JSON matching the requested schema — no markdown, no prose.
            """;

        var parameters = new MessageParameters
        {
            Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }],
            MaxTokens = 4000,
            Model = _modelName,
            Temperature = 0.1m,
            System = [new SystemMessage(system)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters);
        return response.Message.ToString() ?? string.Empty;
    }

    private async Task<string> CallOpenAiAsync(string prompt)
    {
        var client = new OpenAIResponseClient(_modelName, _apiKey!);

        const string instructions = """
            You are a senior software analyst. Read the provided source code carefully and extract a precise,
            factual understanding of the feature. Ground every observation in what the code literally does.
            Return only valid JSON matching the requested schema — no markdown, no prose.
            """;

        var options = new ResponseCreationOptions
        {
            Instructions = instructions,
            MaxOutputTokenCount = 4000,
            Temperature = 0.1f
        };

        var response = await client.CreateResponseAsync(prompt, options);
        return response.Value.GetOutputText();
    }

    private static string StripJsonFences(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
        if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    private static ContextDocument MapToContextDocument(
        AiGeneratedContext ai,
        PipelineRun run,
        RepositoryWorkspace workspace,
        RepositoryAnalysis analysis) => new()
    {
        RunId          = run.RunId,
        Prompt         = run.Prompt,
        ModuleName     = run.ModuleName,
        GeneratedUtc   = DateTimeOffset.UtcNow,
        Repository     = workspace,
        Summary        = ai.Summary,
        BusinessRules  = MapStatements(ai.BusinessRules,  "BUSINESS-RULE"),
        Validations    = MapStatements(ai.Validations,    "VALIDATION"),
        Flows          = MapStatements(ai.Flows,          "FLOW"),
        Dependencies   = MapStatements(ai.Dependencies,   "DEP"),
        EdgeCases      = MapStatements(ai.EdgeCases,      "EDGE"),
        Assumptions    = MapStatements(ai.Assumptions,    "ASSUMP"),
        OpenQuestions  = MapStatements(ai.OpenQuestions,  "Q"),
        EvidenceFiles  = analysis.SelectedFiles
    };

    private static IReadOnlyList<ContextStatement> MapStatements(List<AiContextStatement> items, string prefix) =>
        items.Select((item, i) => new ContextStatement
        {
            Id          = $"{prefix}-{i + 1}",
            Category    = prefix.ToLowerInvariant().Replace('-', '_'),
            Text        = item.Text,
            EvidenceIds = [],
            Confidence  = item.Confidence
        }).ToList();

    // ── Internal JSON models (LLM output) ─────────────────────────────────────

    private sealed record AiGeneratedContext
    {
        [JsonPropertyName("summary")]        public string Summary       { get; init; } = string.Empty;
        [JsonPropertyName("business_rules")] public List<AiContextStatement> BusinessRules  { get; init; } = [];
        [JsonPropertyName("validations")]    public List<AiContextStatement> Validations    { get; init; } = [];
        [JsonPropertyName("flows")]          public List<AiContextStatement> Flows          { get; init; } = [];
        [JsonPropertyName("dependencies")]   public List<AiContextStatement> Dependencies   { get; init; } = [];
        [JsonPropertyName("edge_cases")]     public List<AiContextStatement> EdgeCases      { get; init; } = [];
        [JsonPropertyName("assumptions")]    public List<AiContextStatement> Assumptions    { get; init; } = [];
        [JsonPropertyName("open_questions")] public List<AiContextStatement> OpenQuestions  { get; init; } = [];
    }

    private sealed record AiContextStatement
    {
        [JsonPropertyName("text")]       public string Text       { get; init; } = string.Empty;
        [JsonPropertyName("confidence")] public string Confidence { get; init; } = "medium";
    }
}
