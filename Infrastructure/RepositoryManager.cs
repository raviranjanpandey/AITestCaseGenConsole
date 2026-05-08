using System.Text.RegularExpressions;
using TestAIPoc.Models;

namespace TestAIPoc.Infrastructure;

public sealed class RepositoryManager
{
    private readonly GitCommandRunner _git;
    private readonly string _cacheRoot;

    public RepositoryManager(GitCommandRunner git, string cacheRoot)
    {
        _git = git;
        _cacheRoot = cacheRoot;
    }

    public async Task<RepositoryWorkspace> PrepareAsync(RepositorySpec spec)
    {
        var repoKey = Hashing.Sha256(spec.Describe())[..16];

        if (spec.Kind == RepositorySpecKind.LocalPath)
        {
            var sourcePath = Path.GetFullPath(spec.Location);
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException($"Repository path not found: {sourcePath}");
            }

            if (Directory.Exists(Path.Combine(sourcePath, ".git")))
            {
                var commit = await TryGetGitValueAsync(sourcePath, "rev-parse", "HEAD") ?? "unknown";
                var branch = await TryGetGitValueAsync(sourcePath, "rev-parse", "--abbrev-ref", "HEAD");
                return new RepositoryWorkspace
                {
                    RepoKey = repoKey,
                    SourceDescription = spec.Describe(),
                    RepositoryPath = sourcePath,
                    CachePath = sourcePath,
                    CommitSha = commit,
                    Branch = branch,
                    IsGitRepository = true
                };
            }

            return new RepositoryWorkspace
            {
                RepoKey = repoKey,
                SourceDescription = spec.Describe(),
                RepositoryPath = sourcePath,
                CachePath = sourcePath,
                CommitSha = "unknown",
                Branch = null,
                IsGitRepository = false
            };
        }

        var repoCache = Path.Combine(_cacheRoot, repoKey);
        Directory.CreateDirectory(_cacheRoot);

        if (!Directory.Exists(Path.Combine(repoCache, ".git")))
        {
            if (Directory.Exists(repoCache))
            {
                Directory.Delete(repoCache, true);
            }

            await _git.RunAsync(_cacheRoot, "clone", spec.Location, repoCache);
        }
        else
        {
            await _git.RunAsync(repoCache, "fetch", "--all", "--prune");
        }

        if (!string.IsNullOrWhiteSpace(spec.Branch))
        {
            await _git.RunAsync(repoCache, "checkout", spec.Branch!);
            await _git.RunAsync(repoCache, "pull", "--ff-only");
        }

        if (!string.IsNullOrWhiteSpace(spec.Commit))
        {
            await _git.RunAsync(repoCache, "checkout", spec.Commit!);
        }

        var commitSha = await TryGetGitValueAsync(repoCache, "rev-parse", "HEAD") ?? "unknown";
        var branchName = await TryGetGitValueAsync(repoCache, "rev-parse", "--abbrev-ref", "HEAD");

        return new RepositoryWorkspace
        {
            RepoKey = repoKey,
            SourceDescription = spec.Describe(),
            RepositoryPath = repoCache,
            CachePath = repoCache,
            CommitSha = commitSha,
            Branch = branchName,
            IsGitRepository = true
        };
    }

    private async Task<string?> TryGetGitValueAsync(string workingDirectory, params string[] args)
    {
        try
        {
            var result = await _git.RunAsync(workingDirectory, args);
            return string.IsNullOrWhiteSpace(result.Output) ? null : result.Output.Trim();
        }
        catch
        {
            return null;
        }
    }
}

