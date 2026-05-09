using System.Text;
using System.Text.Json;
using TestAIPoc.Analysis;
using TestAIPoc.Infrastructure;
using TestAIPoc.Models;
using TestAIPoc.Persistence;

namespace TestAIPoc;

public static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(CliOptions.HelpText);
            return 0;
        }

        var repositorySpec = options.BuildRepositorySpec();
        var clientSpec = options.BuildClientRepositorySpec();
        if (repositorySpec is null)
        {
            Console.Error.WriteLine("Provide --server-path / --repo-path, or --repo-url.");
            Console.WriteLine(CliOptions.HelpText);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            Console.Error.WriteLine("Provide --prompt with the module description or user request.");
            return 1;
        }

        var outputRoot = Path.GetFullPath(options.OutputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts"));
        Directory.CreateDirectory(outputRoot);

        var cacheRoot = Path.GetFullPath(options.CacheDir ?? Path.Combine(outputRoot, "cache"));
        Directory.CreateDirectory(cacheRoot);

        var runId = $"run_{DateTime.UtcNow:yyyyMMddHHmmss}_{ShortHash(options.Prompt)}";
        var runDir = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(runDir);

        var repositoryManager = new RepositoryManager(new GitCommandRunner(), cacheRoot);
        var analyzer = new RepositoryAnalyzer();
        var contextComposer = new ContextComposer();
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "Set the OPENAI_API_KEY environment variable before running.");
        var modelName = "gpt-5.4-mini";
        var testCaseComposer = new OpenAiTestCaseComposer(modelName: modelName, apiKey: apiKey);
        var store = new SqliteRunStore(options.DatabasePath ?? Path.Combine(runDir, "testcases.db"));

        var repositorySource = clientSpec is not null
            ? $"{repositorySpec.Describe()} + client:{clientSpec.Location}"
            : repositorySpec.Describe();

        var run = new PipelineRun
        {
            RunId = runId,
            Prompt = options.Prompt,
            ModuleName = options.Module ?? options.Prompt,
            CreatedUtc = DateTimeOffset.UtcNow,
            RepositorySource = repositorySource,
            SystemInstruction = options.SystemInstruction
        };

        WriteStage(1, 4, "Repository Resolution");
        WriteStep($"Server source: {repositorySpec.Describe()}");
        WriteStep("Preparing server workspace...");
        var workspace = await repositoryManager.PrepareAsync(repositorySpec);
        WriteStep($"Server workspace: {workspace.RepositoryPath}");
        WriteStep($"Server commit:    {workspace.CommitSha}");

        RepositoryWorkspace? clientWorkspace = null;
        if (clientSpec is not null)
        {
            WriteStep($"Client source: {clientSpec.Describe()}");
            WriteStep("Preparing client workspace...");
            clientWorkspace = await repositoryManager.PrepareAsync(clientSpec);
            WriteStep($"Client workspace: {clientWorkspace.RepositoryPath}");
            WriteStep($"Client commit:    {clientWorkspace.CommitSha}");
        }

        WriteStage(2, 4, "Repository Analysis");
        WriteStep("Scanning source files and scoring relevance...");
        var analysis = clientWorkspace is not null
            ? analyzer.Analyze(workspace, clientWorkspace, options.Module ?? options.Prompt, options.MaxFiles)
            : analyzer.Analyze(workspace, options.Module ?? options.Prompt, options.MaxFiles);

        var stackType = LanguageDetector.GetStackType(analysis.DetectedLanguages);
        WriteStep($"Detected: {stackType} — {string.Join(", ", analysis.DetectedLanguages)}");
        WriteStep($"Selected {analysis.SelectedFiles.Count} relevant file(s)" +
            (clientWorkspace is not null
                ? $" ({analysis.SelectedFiles.Count(f => f.Layer == "server")} server, {analysis.SelectedFiles.Count(f => f.Layer == "client")} client)"
                : string.Empty) + ".");
        WriteStep($"Extracted {analysis.SearchTerms.Count} search term(s) from the prompt.");

        WriteStage(3, 4, "Context Generation");
        WriteStep("Synthesizing business rules, validations, flows, dependencies, and edge cases...");
        var context = contextComposer.Compose(run, workspace, analysis);
        WriteStep($"Context summary built with {context.BusinessRules.Count} business rule(s) and {context.Validations.Count} validation(s).");
        WriteStep("Writing context artifacts to disk...");

        var contextJson = JsonSerializer.Serialize(context, JsonOptions.Pretty);
        var contextPath = options.ContextJsonPath ?? Path.Combine(runDir, "context.json");
        await File.WriteAllTextAsync(contextPath, contextJson, Encoding.UTF8);

        var contextMarkdown = contextComposer.RenderMarkdown(context);
        var contextMarkdownPath = options.ContextMarkdownPath ?? Path.Combine(runDir, "context.md");
        await File.WriteAllTextAsync(contextMarkdownPath, contextMarkdown, Encoding.UTF8);
        WriteStep($"Context JSON:     {contextPath}");
        WriteStep($"Context Markdown: {contextMarkdownPath}");

        WriteStage(4, 4, "Test Generation and Persistence");
        WriteStep("Calling the LLM-backed testcase generator...");
        var generation = await testCaseComposer.ComposeAsync(run, context, analysis);
        WriteStep($"Generated {generation.TestCases.Count} testcase(s) using {generation.Provider} / {generation.ModelName}.");
        WriteStep($"Test cases will be saved to: {Path.GetFullPath(store.DatabasePath)}");
        WriteStep("Persisting run, context, evidence, and testcase records to SQLite...");
        var result = new PipelineResult(
            run,
            workspace,
            analysis,
            context,
            generation.TestCases,
            contextPath,
            contextMarkdownPath,
            generation.Provider,
            generation.ModelName,
            generation.RawJson);
        await store.SaveAsync(result, options.OverwriteDatabase);
        WriteStep($"SQLite database: {Path.GetFullPath(store.DatabasePath)}");

        Console.WriteLine();
        Console.WriteLine("Run complete.");
        Console.WriteLine($"Run ID:   {run.RunId}");
        Console.WriteLine($"Server:   {workspace.RepositoryPath}");
        if (clientWorkspace is not null)
            Console.WriteLine($"Client:   {clientWorkspace.RepositoryPath}");
        Console.WriteLine($"Stack:    {stackType}");
        Console.WriteLine($"Files:    {analysis.SelectedFiles.Count}");
        Console.WriteLine($"Rules:    {context.BusinessRules.Count + context.Validations.Count}");
        Console.WriteLine($"Tests:    {generation.TestCases.Count}");
        Console.WriteLine($"Gen:      {generation.Provider} / {generation.ModelName}");
        Console.WriteLine($"Context:  {Path.GetFullPath(contextPath)}");
        Console.WriteLine($"Markdown: {Path.GetFullPath(contextMarkdownPath)}");
        Console.WriteLine($"Database: {Path.GetFullPath(store.DatabasePath)}");

        return 0;
    }

    private static string ShortHash(string value)
    {
        var hash = Hashing.Sha256(value);
        return hash[..10];
    }

    private static void WriteStage(int current, int total, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Stage {current}/{total}: {title} ===");
    }

    private static void WriteStep(string message)
    {
        Console.WriteLine($"  - {message}");
    }
}
