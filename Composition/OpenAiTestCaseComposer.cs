using System.Text.Json;
using OpenAI.Responses;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc.Composition;

public sealed class OpenAiTestCaseComposer : ITestCaseComposer
{
    private const int MinTestCases = 6;
    private const int MaxAttempts = 3;

    private readonly string _modelName;
    private readonly string? _apiKey;
    private readonly HeuristicTestCaseComposer _fallbackComposer = new();

    public OpenAiTestCaseComposer(string modelName, string? apiKey)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "gpt-5.4-mini" : modelName;
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
                    repairPrompt = CompositionPromptBuilder.BuildRepairPrompt(context, batch.RawJson, validationError);
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
                repairPrompt = CompositionPromptBuilder.BuildRepairPrompt(context, lastRawJson ?? string.Empty, ex.Message);
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
        var input = CompositionPromptBuilder.BuildPrompt(run, contextJson, analysis, repairPrompt);

        const string defaultInstructions = """
            You are a senior QA engineer writing executable test cases for a software feature.

            Non-negotiable rules:
            1. USER PERSPECTIVE ONLY — every test case is written as an end user navigating the application, not as a developer inspecting internals.
            2. NO ARCHITECTURE TERMS — do not use "server-side", "client-side", "API", "HTTP", "endpoint", "REST", "React", "component", "database", "backend", "frontend", "payload", or "request body" in Title, Steps, or Expected Results.
            3. FEATURE-BASED — group tests by user-facing feature behavior, not by technical layer.
            4. BUSINESS LANGUAGE — use domain terms: "the user submits the form", "the application shows a confirmation", "the record appears in the list".
            5. EXECUTABLE — a non-technical QA analyst must be able to follow each step against a running application.
            6. COMPLETE JOURNEY — each test case covers a meaningful user journey from action to visible outcome.

            Return only JSON that matches the provided schema.
            """;

        var instructions = string.IsNullOrWhiteSpace(run.SystemInstruction)
            ? defaultInstructions
            : defaultInstructions + "\n\nAdditional requirements:\n" + run.SystemInstruction;

        var options = new ResponseCreationOptions
        {
            Instructions = instructions,
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
            .Select(testCase => testCase with
            {
                Severity = CompositionHelpers.DeriveSeverity(testCase.Category, testCase.Risk),
                TestType = CompositionHelpers.DeriveTestType(testCase.Category),
                AutomationSuitability = CompositionHelpers.DeriveAutomationSuitability(testCase.Category, testCase.Risk)
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
        ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException;

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

    private sealed record GeneratedBatchEnvelope(GeneratedTestCaseBatch Batch, string RawJson);
}
