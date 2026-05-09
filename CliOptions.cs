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
    public string? Model { get; init; }
    public int MaxFiles { get; init; } = 18;
    public bool OverwriteDatabase { get; init; } = true;
    public bool ShowHelp { get; init; }
    public string? SystemInstruction { get; init; }

    public RepositorySpec? BuildRepositorySpec()
    {
        var path = RepoPath ?? ServerPath;
        if (!string.IsNullOrWhiteSpace(path))
            return RepositorySpec.FromLocalPath(path!);

        if (!string.IsNullOrWhiteSpace(RepoUrl))
            return RepositorySpec.FromRemote(RepoUrl!, Branch, Commit);

        return null;
    }

    public RepositorySpec? BuildClientRepositorySpec() =>
        !string.IsNullOrWhiteSpace(ClientPath)
            ? RepositorySpec.FromLocalPath(ClientPath!)
            : null;

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
            Model = map.TryGetValue("model", out var model) ? model : null,
            MaxFiles = map.TryGetValue("max-files", out var maxFiles) && int.TryParse(maxFiles, out var parsedMaxFiles)
                ? Math.Clamp(parsedMaxFiles, 5, 75)
                : 18,
            OverwriteDatabase = !map.TryGetValue("append-db", out var appendDb) || !string.Equals(appendDb, "true", StringComparison.OrdinalIgnoreCase),
            ShowHelp = map.ContainsKey("help"),
            SystemInstruction = map.TryGetValue("system-instruction", out var si) ? si : null
        };
    }

    public static string HelpText => """
TestAIPoc

Usage:
  --server-path <path>        Server-side (.NET Web API) repository path
  --client-path <path>        Client-side (React / frontend) repository path
  --repo-path <path>          Single repository path (alternative to --server-path)
  --repo-url <url>            Git or CodeCommit remote URL
  --branch <name>             Branch to checkout when cloning remote repos
  --commit <sha>              Commit to checkout when cloning remote repos
  --module <name>             Module name or area, e.g. Leave Apply
  --prompt <text>             User prompt that describes the test generation request
  --cache-dir <path>          Cache root for cloned workspaces
  --output-dir <path>         Directory for context artifacts
  --db <path>                 SQLite database path
  --context-json <path>       Override path for context JSON output
  --context-md <path>         Override path for context markdown output
  --model <name>              OpenAI model name, defaults to OPENAI_MODEL or gpt-5.1
  --max-files <n>             Maximum relevant files to inspect (default 18, max 75)
  --append-db                 Preserve existing SQLite rows instead of recreating the database
  --system-instruction <text> Override the default OpenAI system instruction for test generation

Examples:
  dotnet run -- --server-path D:\hrm-api --client-path D:\hrm-frontend --module "Leave Apply" --prompt "Generate tests for leave application flow"
  dotnet run -- --repo-path /path/to/app --module "Leave Apply" --prompt "Generate tests for leave application flow"
  dotnet run -- --repo-url https://git-codecommit.us-east-1.amazonaws.com/v1/repos/MyRepo --branch main --module "Leave Apply" --prompt "Generate tests for leave application flow"
""";
}
