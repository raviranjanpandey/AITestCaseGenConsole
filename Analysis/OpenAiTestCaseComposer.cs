using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Responses;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Analysis;

public sealed class OpenAiTestCaseComposer
{
    private const int MinTestCases = 6;
    private const int MaxAttempts = 3;

    private readonly string _modelName;
    private readonly string? _apiKey;
    private readonly TestCaseComposer _fallbackComposer = new();

    public OpenAiTestCaseComposer(string modelName, string? apiKey)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "gpt-5.1" : modelName;
        _apiKey = apiKey;
    }

    public async Task<TestCaseGenerationResult> ComposeAsync(PipelineRun run, ContextDocument context, RepositoryAnalysis analysis)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Console.Error.WriteLine("[warn] OPENAI_API_KEY is not set. Falling back to heuristic test generation.");
            Console.WriteLine("  - No API key found. Using local fallback generator.");
            return BuildFallbackResult(run, context, analysis, "no_api_key");
        }

        var client = new OpenAIResponseClient(_modelName, _apiKey);
        var retryDelay = TimeSpan.FromMilliseconds(500);
        string? repairPrompt = null;
        string? lastRawJson = null;
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            Console.WriteLine($"  - LLM attempt {attempt}/{MaxAttempts}: requesting structured testcase JSON from {_modelName}...");
            try
            {
                var batch = await GenerateBatchAsync(client, run, context, analysis, repairPrompt);
                lastRawJson = batch.RawJson;

                if (!TryValidateBatch(batch.Batch, out var validationError))
                {
                    lastError = validationError;
                    Console.WriteLine($"  - Validation failed: {validationError}");
                    repairPrompt = BuildRepairPrompt(context, batch.RawJson, validationError);
                    Console.WriteLine("  - Scheduling repair pass with the model...");
                }
                else
                {
                    Console.WriteLine($"  - Model returned {batch.Batch.TestCases.Count} testcase(s).");
                    var testCases = batch.Batch.TestCases
                        .Select((generated, index) => ConvertToDocument(run, context, generated, index))
                        .ToList();

                    return new TestCaseGenerationResult(
                        testCases,
                        "openai-responses",
                        _modelName,
                        batch.RawJson);
                }
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsRetryable(ex))
            {
                lastError = ex.Message;
                Console.Error.WriteLine($"[warn] OpenAI attempt {attempt} failed: {ex.Message}");
                Console.WriteLine($"  - Transient failure, retrying in {retryDelay.TotalMilliseconds:0} ms...");
                await Task.Delay(retryDelay);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 4000));
                continue;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastError = ex.Message;
                Console.Error.WriteLine($"[warn] OpenAI attempt {attempt} returned invalid output: {ex.Message}");
                repairPrompt = BuildRepairPrompt(context, lastRawJson ?? string.Empty, ex.Message);
                Console.WriteLine($"  - Invalid output, retrying in {retryDelay.TotalMilliseconds:0} ms...");
                await Task.Delay(retryDelay);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 4000));
                continue;
            }
        }

        Console.Error.WriteLine($"[warn] OpenAI generation did not yield a valid batch after {MaxAttempts} attempts: {lastError}");
        Console.WriteLine("  - Falling back to heuristic testcase generation.");
        return BuildFallbackResult(run, context, analysis, "openai_failed_after_retries");
    }

    private async Task<GeneratedBatchEnvelope> GenerateBatchAsync(
        OpenAIResponseClient client,
        PipelineRun run,
        ContextDocument context,
        RepositoryAnalysis analysis,
        string? repairPrompt)
    {
        var contextJson = JsonSerializer.Serialize(context, JsonOptions.Pretty);
        var input = BuildPrompt(run, contextJson, analysis, repairPrompt);

        var options = new ResponseCreationOptions
        {
            Instructions = """
                You are a senior test design assistant.
                Generate realistic, module-specific test cases from the supplied repository context.
                Prefer concrete business rules, validations, flows, edge cases, and authorization scenarios.
                Do not invent new source facts when context is missing. Use the notes field for assumptions.
                Return only JSON that matches the provided schema.
                """,
            MaxOutputTokenCount = 7000,
            Temperature = 0.2f,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "test_case_generation",
                    jsonSchema: BinaryData.FromBytes(TestCaseGenerationSchema.JsonSchemaUtf8),
                    jsonSchemaFormatDescription: "Structured testcase batch generated from repository context.",
                    jsonSchemaIsStrict: true)
            }
        };

        var response = await client.CreateResponseAsync(input, options);
        var rawJson = response.Value.GetOutputText();
        Console.WriteLine("  - OpenAI Responses API returned structured text output.");
        var batch = JsonSerializer.Deserialize<GeneratedTestCaseBatch>(rawJson, JsonOptions.Strict)
                    ?? throw new InvalidOperationException("The model returned empty or invalid JSON.");

        return new GeneratedBatchEnvelope(batch, rawJson);
    }

    private TestCaseGenerationResult BuildFallbackResult(PipelineRun run, ContextDocument context, RepositoryAnalysis analysis, string reason)
    {
        var fallback = _fallbackComposer.Compose(run, context, analysis)
            .Select((testCase, index) => testCase with
            {
                Severity = DeriveSeverity(testCase.Category, testCase.Risk),
                TestType = DeriveTestType(testCase.Category),
                AutomationSuitability = DeriveAutomationSuitability(testCase.Category, testCase.Risk)
            })
            .ToList();

        var fallbackBatch = new GeneratedTestCaseBatch
        {
            Model = "heuristic-fallback",
            Summary = $"Generated locally because OpenAI generation was unavailable or invalid ({reason}).",
            TestCases = fallback.Select(ConvertToGeneratedTestCase).ToList()
        };

        Console.WriteLine($"  - Fallback generator produced {fallback.Count} testcase(s).");
        return new TestCaseGenerationResult(
            fallback,
            "heuristic-fallback",
            "heuristic-fallback",
            JsonSerializer.Serialize(fallbackBatch, JsonOptions.Pretty));
    }

    private static bool TryValidateBatch(GeneratedTestCaseBatch batch, out string error)
    {
        if (batch.TestCases.Count < MinTestCases)
        {
            error = $"Expected at least {MinTestCases} test cases but received {batch.TestCases.Count}.";
            return false;
        }

        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var testCase in batch.TestCases)
        {
            if (string.IsNullOrWhiteSpace(testCase.Title)
                || string.IsNullOrWhiteSpace(testCase.Category)
                || string.IsNullOrWhiteSpace(testCase.Severity)
                || string.IsNullOrWhiteSpace(testCase.TestType)
                || string.IsNullOrWhiteSpace(testCase.AutomationSuitability)
                || string.IsNullOrWhiteSpace(testCase.Priority)
                || string.IsNullOrWhiteSpace(testCase.Risk)
                || testCase.Preconditions.Count == 0
                || testCase.Steps.Count == 0
                || testCase.ExpectedResults.Count == 0
                || testCase.Traceability.Count == 0)
            {
                error = $"One or more required fields were empty for testcase '{testCase.Title}'.";
                return false;
            }

            if (!seenTitles.Add(testCase.Title))
            {
                error = $"Duplicate testcase title found: {testCase.Title}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsRetryable(Exception ex) =>
        ex is HttpRequestException
        || ex is TaskCanceledException
        || ex is TimeoutException
        || ex is IOException;

    private static string BuildRepairPrompt(ContextDocument context, string rawJson, string reason)
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

    private string BuildPrompt(
        PipelineRun run,
        string contextJson,
        RepositoryAnalysis analysis,
        string? repairPrompt)
    {
        var evidenceSummary = string.Join(Environment.NewLine, analysis.SelectedFiles.Select(file =>
        {
            var evidenceIds = string.Join(", ", file.Evidence.Select(evidence => evidence.EvidenceId).Take(3));
            return $"- {file.RelativePath} | score={file.Score:F1} | evidence={evidenceIds}";
        }));

        var builder = new StringBuilder();
        builder.AppendLine($"Target module: {run.ModuleName}");
        builder.AppendLine($"User prompt: {run.Prompt}");
        builder.AppendLine($"Repository commit: {run.RepositorySource}");
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
        builder.AppendLine("Every testcase must include:");
        builder.AppendLine("- title");
        builder.AppendLine("- category");
        builder.AppendLine("- severity");
        builder.AppendLine("- test_type");
        builder.AppendLine("- automation_suitability");
        builder.AppendLine("- priority");
        builder.AppendLine("- risk");
        builder.AppendLine("- preconditions");
        builder.AppendLine("- steps");
        builder.AppendLine("- expected_results");
        builder.AppendLine("- traceability");
        builder.AppendLine("- notes");
        builder.AppendLine();
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

    private static GeneratedTestCase ConvertToGeneratedTestCase(TestCaseDocument testCase) =>
        new()
        {
            Title = testCase.Title,
            Category = testCase.Category,
            Severity = testCase.Severity,
            TestType = testCase.TestType,
            AutomationSuitability = testCase.AutomationSuitability,
            Priority = testCase.Priority,
            Risk = testCase.Risk,
            Preconditions = testCase.Preconditions.ToList(),
            Steps = testCase.Steps.ToList(),
            ExpectedResults = testCase.ExpectedResults.ToList(),
            Traceability = testCase.Traceability.ToList(),
            Notes = testCase.Notes
        };

    private static TestCaseDocument ConvertToDocument(PipelineRun run, ContextDocument context, GeneratedTestCase generated, int index)
    {
        var keySource = $"{run.RunId}|{context.Repository.CommitSha}|{generated.Title}|{generated.Category}|{index}";
        var id = $"TC-{Hashing.Sha256(keySource)[..12]}";

        return new TestCaseDocument
        {
            Id = id,
            Title = generated.Title,
            Category = generated.Category,
            Severity = generated.Severity,
            TestType = generated.TestType,
            AutomationSuitability = generated.AutomationSuitability,
            Priority = generated.Priority,
            Risk = generated.Risk,
            Preconditions = generated.Preconditions,
            Steps = generated.Steps,
            ExpectedResults = generated.ExpectedResults,
            Traceability = generated.Traceability,
            Notes = generated.Notes,
            OriginCommit = context.Repository.CommitSha,
            RunId = run.RunId
        };
    }

    private static string DeriveSeverity(string category, string risk) =>
        category switch
        {
            "security" => "high",
            "validation" => "high",
            "negative" => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "high" : "medium",
            "boundary" => "medium",
            _ => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "high" : "medium"
        };

    private static string DeriveTestType(string category) =>
        category switch
        {
            "validation" => "validation",
            "negative" => "negative",
            "boundary" => "boundary",
            "security" => "security",
            "functional" => "functional",
            _ => "regression"
        };

    private static string DeriveAutomationSuitability(string category, string risk) =>
        category switch
        {
            "security" => "partial",
            "negative" => "automatable",
            "boundary" => "automatable",
            "validation" => "automatable",
            _ => risk.Equals("high", StringComparison.OrdinalIgnoreCase) ? "partial" : "automatable"
        };

    private sealed record GeneratedBatchEnvelope(GeneratedTestCaseBatch Batch, string RawJson);
}

public sealed record TestCaseGenerationResult(
    IReadOnlyList<TestCaseDocument> TestCases,
    string Provider,
    string ModelName,
    string RawJson);

public sealed record GeneratedTestCaseBatch
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("test_cases")]
    public List<GeneratedTestCase> TestCases { get; init; } = [];
}

public sealed record GeneratedTestCase
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("test_type")]
    public string TestType { get; init; } = string.Empty;

    [JsonPropertyName("automation_suitability")]
    public string AutomationSuitability { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = string.Empty;

    [JsonPropertyName("risk")]
    public string Risk { get; init; } = string.Empty;

    [JsonPropertyName("preconditions")]
    public List<string> Preconditions { get; init; } = [];

    [JsonPropertyName("steps")]
    public List<string> Steps { get; init; } = [];

    [JsonPropertyName("expected_results")]
    public List<string> ExpectedResults { get; init; } = [];

    [JsonPropertyName("traceability")]
    public List<string> Traceability { get; init; } = [];

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;
}

internal static class TestCaseGenerationSchema
{
    public static byte[] JsonSchemaUtf8 => """
    {
      "type": "object",
      "properties": {
        "model": { "type": "string" },
        "summary": { "type": "string" },
        "test_cases": {
          "type": "array",
          "minItems": 6,
          "items": {
            "type": "object",
            "properties": {
              "title": { "type": "string" },
              "category": { "type": "string" },
              "severity": { "type": "string", "enum": ["critical", "high", "medium", "low"] },
              "test_type": { "type": "string", "enum": ["functional", "validation", "negative", "boundary", "security", "integration", "regression"] },
              "automation_suitability": { "type": "string", "enum": ["automatable", "partial", "manual"] },
              "priority": { "type": "string", "enum": ["high", "medium", "low"] },
              "risk": { "type": "string", "enum": ["high", "medium", "low"] },
              "preconditions": { "type": "array", "items": { "type": "string" } },
              "steps": { "type": "array", "items": { "type": "string" } },
              "expected_results": { "type": "array", "items": { "type": "string" } },
              "traceability": { "type": "array", "items": { "type": "string" } },
              "notes": { "type": "string" }
            },
            "required": ["title", "category", "severity", "test_type", "automation_suitability", "priority", "risk", "preconditions", "steps", "expected_results", "traceability", "notes"],
            "additionalProperties": false
          }
        }
      },
      "required": ["model", "summary", "test_cases"],
      "additionalProperties": false
    }
    """u8.ToArray();
}
