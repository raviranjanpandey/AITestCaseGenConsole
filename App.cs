using System.Text;
using System.Text.Json;
using TestAIPoc.Analysis;
using TestAIPoc.Composition;
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
            return ExitCodes.Success;
        }

        var repositorySpec = options.BuildRepositorySpec();
        var clientSpec = options.BuildClientRepositorySpec();

        if (repositorySpec is null)
        {
            Console.Error.WriteLine("Provide --server-path / --repo-path, or --repo-url.");
            Console.WriteLine(CliOptions.HelpText);
            return ExitCodes.BadArguments;
        }

        if (string.IsNullOrWhiteSpace(options.Prompt))
        {
            Console.Error.WriteLine("Provide --prompt with the module description or user request.");
            return ExitCodes.BadArguments;
        }

        var outputRoot = Path.GetFullPath(options.OutputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts"));
        Directory.CreateDirectory(outputRoot);

        var cacheRoot = Path.GetFullPath(options.CacheDir ?? Path.Combine(outputRoot, "cache"));
        Directory.CreateDirectory(cacheRoot);

        var runId = $"run_{DateTime.UtcNow:yyyyMMddHHmmss}_{ShortHash(options.Prompt)}";
        var runDir = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(runDir);

        var apiKey = options.ResolveApiKey();
        var provider = options.Provider ?? "openai";

        var repositoryManager = new RepositoryManager(new GitCommandRunner(), cacheRoot);
        var analyzer = new RepositoryAnalyzer();
        var contextComposer = new ContextComposer();
        var testCaseComposer = TestCaseComposerFactory.Create(provider, options.Model, apiKey);
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

        // ============================================================
        // Stage 1: Repository Resolution
        // ============================================================
        WriteStage(1, 4, "Repository Resolution");
        RepositoryWorkspace workspace;
        RepositoryWorkspace? clientWorkspace = null;
        try
        {
            WriteStep($"Server source: {repositorySpec.Describe()}");
            WriteStep("Preparing server workspace...");
            workspace = await repositoryManager.PrepareAsync(repositorySpec);
            WriteStep($"Server workspace: {workspace.RepositoryPath}");
            WriteStep($"Server commit:    {workspace.CommitSha}");

            if (clientSpec is not null)
            {
                WriteStep($"Client source: {clientSpec.Describe()}");
                WriteStep("Preparing client workspace...");
                clientWorkspace = await repositoryManager.PrepareAsync(clientSpec);
                WriteStep($"Client workspace: {clientWorkspace.RepositoryPath}");
                WriteStep($"Client commit:    {clientWorkspace.CommitSha}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Stage 1 — Repository resolution failed: {ex.Message}");
            return ExitCodes.RepositoryError;
        }

        // ============================================================
        // Stage 2: Repository Analysis
        // ============================================================
        WriteStage(2, 4, "Repository Analysis");
        RepositoryAnalysis analysis;
        try
        {
            WriteStep("Scanning source files and scoring relevance...");
            analysis = clientWorkspace is not null
                ? analyzer.Analyze(workspace, clientWorkspace, options.Module ?? options.Prompt, options.MaxFiles)
                : analyzer.Analyze(workspace, options.Module ?? options.Prompt, options.MaxFiles);

            var stackType = LanguageDetector.GetStackType(analysis.DetectedLanguages);
            WriteStep($"Detected: {stackType} — {string.Join(", ", analysis.DetectedLanguages)}");
            WriteStep($"Selected {analysis.SelectedFiles.Count} relevant file(s)" +
                (clientWorkspace is not null
                    ? $" ({analysis.SelectedFiles.Count(f => f.Layer == "server")} server, {analysis.SelectedFiles.Count(f => f.Layer == "client")} client)"
                    : string.Empty) + ".");
            WriteStep($"Extracted {analysis.SearchTerms.Count} search term(s) from the prompt.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Stage 2 — Repository analysis failed: {ex.Message}");
            return ExitCodes.RepositoryError;
        }

        // ============================================================
        // Stage 3: Context Generation
        // ============================================================
        WriteStage(3, 4, "Context Generation");
        ContextDocument context;
        string contextPath;
        string contextMarkdownPath;
        try
        {
            WriteStep("Synthesizing business rules, validations, flows, dependencies, and edge cases...");

            if (clientWorkspace is not null)
            {
                // Generate separate contexts per layer, then merge into one unified context.
                var serverOnlyAnalysis = FilterAnalysisByLayer(analysis, "server", workspace);
                var clientOnlyAnalysis = FilterAnalysisByLayer(analysis, "client", clientWorkspace);

                WriteStep($"Composing server-side context ({serverOnlyAnalysis.SelectedFiles.Count} file(s))...");
                var serverContext = contextComposer.Compose(run, workspace, serverOnlyAnalysis);

                WriteStep($"Composing client-side context ({clientOnlyAnalysis.SelectedFiles.Count} file(s))...");
                var clientContext = contextComposer.Compose(run, clientWorkspace, clientOnlyAnalysis);

                WriteStep("Merging server and client contexts...");
                context = contextComposer.MergeContexts(serverContext, clientContext, run, workspace);

                // Save individual layer contexts as side artifacts.
                var serverCtxJson = JsonSerializer.Serialize(serverContext, JsonOptions.Pretty);
                var serverCtxPath = Path.Combine(runDir, "server-context.json");
                await File.WriteAllTextAsync(serverCtxPath, serverCtxJson, Encoding.UTF8);
                await File.WriteAllTextAsync(
                    Path.Combine(runDir, "server-context.md"),
                    contextComposer.RenderMarkdown(serverContext), Encoding.UTF8);

                var clientCtxJson = JsonSerializer.Serialize(clientContext, JsonOptions.Pretty);
                var clientCtxPath = Path.Combine(runDir, "client-context.json");
                await File.WriteAllTextAsync(clientCtxPath, clientCtxJson, Encoding.UTF8);
                await File.WriteAllTextAsync(
                    Path.Combine(runDir, "client-context.md"),
                    contextComposer.RenderMarkdown(clientContext), Encoding.UTF8);

                WriteStep($"Server context: {serverCtxPath}");
                WriteStep($"Client context: {clientCtxPath}");
            }
            else
            {
                context = contextComposer.Compose(run, workspace, analysis);
            }

            WriteStep($"Context built with {context.BusinessRules.Count} business rule(s) and {context.Validations.Count} validation(s).");
            WriteStep("Writing merged context artifacts to disk...");

            var contextJson = JsonSerializer.Serialize(context, JsonOptions.Pretty);
            contextPath = options.ContextJsonPath ?? Path.Combine(runDir, "context.json");
            await File.WriteAllTextAsync(contextPath, contextJson, Encoding.UTF8);

            var contextMarkdown = contextComposer.RenderMarkdown(context);
            contextMarkdownPath = options.ContextMarkdownPath ?? Path.Combine(runDir, "context.md");
            await File.WriteAllTextAsync(contextMarkdownPath, contextMarkdown, Encoding.UTF8);

            WriteStep($"Context JSON:     {contextPath}");
            WriteStep($"Context Markdown: {contextMarkdownPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Stage 3 — Context generation failed: {ex.Message}");
            return ExitCodes.RepositoryError;
        }

        // ============================================================
        // Stage 4: Test Generation and Persistence
        // ============================================================
        WriteStage(4, 4, "Test Generation and Persistence");
        TestCaseGenerationResult generation;
        try
        {
            WriteStep($"Calling {provider} test-case composer...");
            generation = await testCaseComposer.ComposeAsync(run, context, analysis);
            WriteStep($"Generated {generation.TestCases.Count} testcase(s) using {generation.Provider} / {generation.ModelName}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Stage 4 — LLM generation failed: {ex.Message}");
            return ExitCodes.LlmError;
        }

        try
        {
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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] Stage 4 — Persistence failed: {ex.Message}");
            return ExitCodes.PersistenceError;
        }

        Console.WriteLine();
        Console.WriteLine("Run complete.");
        Console.WriteLine($"Run ID:   {run.RunId}");
        Console.WriteLine($"Server:   {workspace.RepositoryPath}");
        if (clientWorkspace is not null)
            Console.WriteLine($"Client:   {clientWorkspace.RepositoryPath}");
        Console.WriteLine($"Stack:    {LanguageDetector.GetStackType(analysis.DetectedLanguages)}");
        Console.WriteLine($"Provider: {generation.Provider}");
        Console.WriteLine($"Files:    {analysis.SelectedFiles.Count}");
        Console.WriteLine($"Rules:    {context.BusinessRules.Count + context.Validations.Count}");
        Console.WriteLine($"Tests:    {generation.TestCases.Count}");
        Console.WriteLine($"Gen:      {generation.Provider} / {generation.ModelName}");
        Console.WriteLine($"Context:  {Path.GetFullPath(contextPath)}");
        Console.WriteLine($"Markdown: {Path.GetFullPath(contextMarkdownPath)}");
        Console.WriteLine($"Database: {Path.GetFullPath(store.DatabasePath)}");

        return ExitCodes.Success;
    }

    private static RepositoryAnalysis FilterAnalysisByLayer(
        RepositoryAnalysis combined, string layer, RepositoryWorkspace workspace) =>
        new(
            Workspace: workspace,
            SelectedFiles: combined.SelectedFiles.Where(f => f.Layer == layer).ToList(),
            SearchTerms: combined.SearchTerms,
            DetectedLanguages: combined.DetectedLanguages,
            PrimaryLanguage: combined.PrimaryLanguage,
            ClientWorkspace: null);

    private static string ShortHash(string value) => Hashing.Sha256(value)[..10];

    private static void WriteStage(int current, int total, string title) =>
        Console.WriteLine($"\n=== Stage {current}/{total}: {title} ===");

    private static void WriteStep(string message) =>
        Console.WriteLine($"  - {message}");
}
