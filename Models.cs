namespace TestAIPoc.Models;

public sealed record PipelineRun
{
    public required string RunId { get; init; }
    public required string Prompt { get; init; }
    public required string ModuleName { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string RepositorySource { get; init; }
    public string? SystemInstruction { get; init; }
}

public sealed record PipelineResult(
    PipelineRun Run,
    RepositoryWorkspace Workspace,
    RepositoryAnalysis Analysis,
    ContextDocument Context,
    IReadOnlyList<TestCaseDocument> TestCases,
    string ContextJsonPath,
    string ContextMarkdownPath,
    string GenerationProvider,
    string ModelName,
    string RawModelJson);

public sealed record RepositorySpec
{
    public required RepositorySpecKind Kind { get; init; }
    public required string Location { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }

    public static RepositorySpec FromLocalPath(string path) => new()
    {
        Kind = RepositorySpecKind.LocalPath,
        Location = Path.GetFullPath(path)
    };

    public static RepositorySpec FromRemote(string url, string? branch, string? commit) => new()
    {
        Kind = RepositorySpecKind.RemoteGit,
        Location = url,
        Branch = branch,
        Commit = commit
    };

    public string Describe() => Kind switch
    {
        RepositorySpecKind.LocalPath => $"local:{Location}",
        _ => $"remote:{Location}:{Branch ?? "default"}:{Commit ?? "HEAD"}"
    };
}

public enum RepositorySpecKind
{
    LocalPath,
    RemoteGit
}

public sealed record RepositoryWorkspace
{
    public required string RepoKey { get; init; }
    public required string SourceDescription { get; init; }
    public required string RepositoryPath { get; init; }
    public required string CachePath { get; init; }
    public required string CommitSha { get; init; }
    public required string? Branch { get; init; }
    public required bool IsGitRepository { get; init; }
}

public sealed record RepositoryAnalysis(
    RepositoryWorkspace Workspace,
    IReadOnlyList<FileInsight> SelectedFiles,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> DetectedLanguages,
    string PrimaryLanguage,
    RepositoryWorkspace? ClientWorkspace);

public sealed record FileInsight
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required string Language { get; init; }
    public required double Score { get; init; }
    public required string WhySelected { get; init; }
    public required IReadOnlyList<string> MatchedTerms { get; init; }
    public required IReadOnlyList<string> Symbols { get; init; }
    public required IReadOnlyList<string> Usings { get; init; }
    public required IReadOnlyList<EvidenceSnippet> Evidence { get; init; }
    public string? Layer { get; init; }  // "server", "client", or null for single-repo
}

public sealed record EvidenceSnippet
{
    public required string EvidenceId { get; init; }
    public required string FilePath { get; init; }
    public required int LineStart { get; init; }
    public required int LineEnd { get; init; }
    public required string Snippet { get; init; }
    public required string Note { get; init; }
    public required double Score { get; init; }
}

public sealed record ContextDocument
{
    public required string RunId { get; init; }
    public required string Prompt { get; init; }
    public required string ModuleName { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required RepositoryWorkspace Repository { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<ContextStatement> BusinessRules { get; init; }
    public required IReadOnlyList<ContextStatement> Validations { get; init; }
    public required IReadOnlyList<ContextStatement> Flows { get; init; }
    public required IReadOnlyList<ContextStatement> Dependencies { get; init; }
    public required IReadOnlyList<ContextStatement> EdgeCases { get; init; }
    public required IReadOnlyList<ContextStatement> Assumptions { get; init; }
    public required IReadOnlyList<ContextStatement> OpenQuestions { get; init; }
    public required IReadOnlyList<FileInsight> EvidenceFiles { get; init; }
}

public sealed record ContextStatement
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<string> EvidenceIds { get; init; }
    public required string Confidence { get; init; }
}

public sealed record TestCaseDocument
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Severity { get; init; }
    public required string TestType { get; init; }
    public required string AutomationSuitability { get; init; }
    public required string Priority { get; init; }
    public required string Risk { get; init; }
    public required IReadOnlyList<string> Preconditions { get; init; }
    public required IReadOnlyList<string> Steps { get; init; }
    public required IReadOnlyList<string> ExpectedResults { get; init; }
    public required IReadOnlyList<string> Traceability { get; init; }
    public required string Notes { get; init; }
    public required string OriginCommit { get; init; }
    public required string RunId { get; init; }
}

public sealed record TestCaseGenerationResult(
    IReadOnlyList<TestCaseDocument> TestCases,
    string Provider,
    string ModelName,
    string RawJson);
