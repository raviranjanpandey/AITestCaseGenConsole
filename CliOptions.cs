using TestAIPoc.Infrastructure;
using TestAIPoc.Models;

namespace TestAIPoc;

public sealed record CliOptions
{
    public string? RepoPath { get; init; }
    public string? ServerPath { get; init; }
    public string? ClientPath { get; init; }
    public string? RepoUrl { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public string? Module { get; init; }
    public string? Prompt { get; init; }
    public string? CacheDir { get; init; }
    public string? OutputDir { get; init; }
    public string? DatabasePath { get; init; }
    public string? ContextJsonPath { get; init; }
    public string? ContextMarkdownPath { get; init; }
    public string? Provider { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public string? SystemInstruction { get; init; }
    public int MaxFiles { get; init; } = 18;
    public bool AiContextEnabled { get; init; }
    public bool OverwriteDatabase { get; init; } = false;
    public bool ShowHelp { get; init; }

    public RepositorySpec? BuildRepositorySpec()
    {
        var path = RepoPath ?? ServerPath;
        if (!string.IsNullOrWhiteSpace(path))
            return RepositorySpec.FromLocalPath(path!);

        if (!string.IsNullOrWhiteSpace(RepoUrl))
            return RepositorySpec.FromRemote(RepoUrl!, Branch, Commit);

        // Client-only mode: treat the client path as the sole repository.
        if (!string.IsNullOrWhiteSpace(ClientPath))
            return RepositorySpec.FromLocalPath(ClientPath!);

        return null;
    }

    public RepositorySpec? BuildClientRepositorySpec()
    {
        // Only activate dual-repo mode when a server path is also present.
        // If only --client-path is given, it becomes the primary workspace above.
        var hasServerPath = !string.IsNullOrWhiteSpace(RepoPath ?? ServerPath);
        return hasServerPath && !string.IsNullOrWhiteSpace(ClientPath)
            ? RepositorySpec.FromLocalPath(ClientPath!)
            : null;
    }

    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            return ApiKey;
        var provider = (Provider ?? "openai").Trim().ToLowerInvariant();
        return provider is "claude" or "anthropic"
            ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }

    public static CliOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "/?")
            {
                map["help"] = "true";
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var keyValue = arg[2..];
            string key;
            string? value;

            var equalsIndex = keyValue.IndexOf('=');
            if (equalsIndex >= 0)
            {
                key = keyValue[..equalsIndex];
                value = keyValue[(equalsIndex + 1)..];
            }
            else
            {
                key = keyValue;
                value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";
            }

            map[key] = value;
        }

        return new CliOptions
        {
            RepoPath = map.TryGetValue("repo-path", out var repoPath) ? repoPath : null,
            ServerPath = map.TryGetValue("server-path", out var serverPath) ? serverPath : null,
            ClientPath = map.TryGetValue("client-path", out var clientPath) ? clientPath : null,
            RepoUrl = map.TryGetValue("repo-url", out var repoUrl) ? repoUrl : null,
            Branch = map.TryGetValue("branch", out var branch) ? branch : null,
            Commit = map.TryGetValue("commit", out var commit) ? commit : null,
            Module = map.TryGetValue("module", out var module) ? module : null,
            Prompt = map.TryGetValue("prompt", out var prompt) ? prompt : null,
            CacheDir = map.TryGetValue("cache-dir", out var cacheDir) ? cacheDir : null,
            OutputDir = map.TryGetValue("output-dir", out var outputDir) ? outputDir : null,
            DatabasePath = map.TryGetValue("db", out var db) ? db : null,
            ContextJsonPath = map.TryGetValue("context-json", out var contextJson) ? contextJson : null,
            ContextMarkdownPath = map.TryGetValue("context-md", out var contextMd) ? contextMd : null,
            Provider = map.TryGetValue("provider", out var provider) ? provider : null,
            ApiKey = map.TryGetValue("api-key", out var apiKey) ? apiKey : null,
            Model = map.TryGetValue("model", out var model) ? model : null,
            SystemInstruction = map.TryGetValue("system-instruction", out var si) ? si : null,
            MaxFiles = map.TryGetValue("max-files", out var maxFiles) && int.TryParse(maxFiles, out var parsedMaxFiles)
                ? Math.Clamp(parsedMaxFiles, 5, 75)
                : 18,
            AiContextEnabled = map.ContainsKey("ai-context"),
            OverwriteDatabase = map.TryGetValue("overwrite-db", out var overwriteDb) && string.Equals(overwriteDb, "true", StringComparison.OrdinalIgnoreCase),
            ShowHelp = map.ContainsKey("help")
        };
    }

    public static string HelpText => """
TestAIPoc — AI-powered test case generator

Usage:
  --server-path <path>        Server-side (.NET Web API) repository path
  --client-path <path>        Client-side (React / frontend) repository path
  --repo-path <path>          Single repository path (alternative to --server-path)
  --repo-url <url>            Git remote URL to clone
  --branch <name>             Branch to checkout when cloning remote repos
  --commit <sha>              Commit SHA to checkout when cloning remote repos
  --module <name>             Module name or area (e.g. "Leave Apply")
  --prompt <text>             User prompt describing the test generation request
  --provider <name>           LLM provider: openai (default) or claude
  --api-key <key>             API key (overrides OPENAI_API_KEY / ANTHROPIC_API_KEY env vars)
  --model <name>              Model name for the selected provider
  --cache-dir <path>          Cache root for cloned workspaces
  --output-dir <path>         Directory for context artifacts
  --db <path>                 SQLite database path
  --context-json <path>       Override path for context JSON output
  --context-md <path>         Override path for context markdown output
  --max-files <n>             Maximum relevant files to inspect (default 18, max 75)
  --ai-context                Enable AI-powered deep code analysis for richer context (uses the selected
                              --provider and --model; adds one extra LLM call before test generation)
  --overwrite-db              Delete and recreate the database on each run (default: records are appended)
  --system-instruction <text> Override the default LLM system instruction for test generation

Examples:
  # OpenAI (default)
  dotnet run -- --repo-path D:\myapp --prompt "Tests for login" --provider openai --api-key sk-...

  # Claude
  dotnet run -- --server-path D:\hrm-api --client-path D:\hrm-frontend --module "Leave Apply" --prompt "Generate tests for leave application flow" --provider claude --api-key sk-ant-...

  # Via environment variable
  $env:ANTHROPIC_API_KEY="sk-ant-..."
  dotnet run -- --repo-path D:\myapp --prompt "Tests for auth" --provider claude --model claude-opus-4-5
""";
}
