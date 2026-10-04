using System.Diagnostics;

namespace Hippo.Tests.E2E;

/// <summary>
/// Runs hippo as a separate process. Set HIPPO_EXE to the absolute path of a published binary to test that binary;
/// otherwise the build output beside the tests runs under the dotnet host. hippo inherits the test runner's
/// environment without its <c>GIT_</c> variables, so git, run by hippo, acts only on the test's workspace.
/// </summary>
public static class HippoProcess
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    public static Task<Result> RunAsync(params string[] args) => RunAsync(null, new Dictionary<string, string>(), args);

    /// <summary>Runs hippo in <paramref name="workingDirectory"/> (the test's own when null) with
    /// <paramref name="environment"/> added to the inherited environment, after its <c>GIT_</c> variables are
    /// removed.</summary>
    public static async Task<Result> RunAsync(string? workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var exe = Environment.GetEnvironmentVariable("HIPPO_EXE");
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }
        RemoveGitVariables(start);
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }
        if (string.IsNullOrEmpty(exe))
        {
            start.FileName = "dotnet";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "hippo.dll"));
        }
        else
        {
            start.FileName = exe;
        }
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(ProcessTimeout);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new Result(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Removes every inherited variable whose name starts with <c>GIT_</c>. Run from a git hook, the test
    /// runner has <c>GIT_DIR</c> (and <c>GIT_INDEX_FILE</c> in pre-commit), which would point git at the outer
    /// repository instead of the workspace.</summary>
    public static void RemoveGitVariables(ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(name);
        }
    }
}
