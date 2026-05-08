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
        if (repositorySpec is null)
        {
            Console.Error.WriteLine("Provide either --repo-path or --repo-url.");
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
        var testCaseComposer = new OpenAiTestCaseComposer(
            modelName: options.Model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5.1",
            apiKey: Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var store = new SqliteRunStore(options.DatabasePath ?? Path.Combine(runDir, "testcases.db"));

        var run = new PipelineRun
        {
            RunId = runId,
            Prompt = options.Prompt,
            ModuleName = options.Module ?? options.Prompt,
            CreatedUtc = DateTimeOffset.UtcNow,
            RepositorySource = repositorySpec.Describe()
        };

        WriteStage(1, 4, "Repository Resolution");
        WriteStep($"Source: {run.RepositorySource}");
        WriteStep("Preparing local workspace and resolving commit metadata...");
        var workspace = await repositoryManager.PrepareAsync(repositorySpec);
        WriteStep($"Workspace ready: {workspace.RepositoryPath}");
        WriteStep($"Commit: {workspace.CommitSha}");

        WriteStage(2, 4, "Repository Analysis");
        WriteStep("Scanning source files and scoring relevance...");
        var analysis = analyzer.Analyze(workspace, options.Module ?? options.Prompt, options.MaxFiles);
        WriteStep($"Selected {analysis.SelectedFiles.Count} relevant file(s).");
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
        WriteStep($"Context JSON: {contextPath}");
        WriteStep($"Context Markdown: {contextMarkdownPath}");

        WriteStage(4, 4, "Test Generation and Persistence");
        WriteStep("Calling the LLM-backed testcase generator...");
        var generation = await testCaseComposer.ComposeAsync(run, context, analysis);
        WriteStep($"Generated {generation.TestCases.Count} testcase(s) using {generation.Provider} / {generation.ModelName}.");
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
        Console.WriteLine($"Repo:     {workspace.RepositoryPath}");
        Console.WriteLine($"Commit:   {workspace.CommitSha}");
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
