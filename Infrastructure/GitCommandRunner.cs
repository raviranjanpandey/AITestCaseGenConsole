using System.Diagnostics;
using System.Text;

namespace TestAIPoc.Infrastructure;

public sealed class GitCommandRunner
{
    public async Task<ProcessResult> RunAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        output.Append(await outputTask.ConfigureAwait(false));
        error.Append(await errorTask.ConfigureAwait(false));

        var result = new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }

        return result;
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);

