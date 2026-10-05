using System.Diagnostics;

namespace Hippo.Tests.E2E;

/// <summary>
/// Runs hippo as a separate process. Set HIPPO_EXE to the absolute path of a binary to test that binary; otherwise the
/// native AOT binary the build publishes into aot/ beside the tests runs, as CI tests one. hippo inherits the test
/// runner's environment without its <c>GIT_</c> variables, so git, run by hippo, acts only on the test's workspace.
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
        var start = new ProcessStartInfo(Exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }
        WorkspaceFixture.RemoveGitVariables(start);
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
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

    /// <summary>HIPPO_EXE when set, otherwise the binary the build publishes, which must exist.</summary>
    private static string Exe
    {
        get
        {
            var exe = Environment.GetEnvironmentVariable("HIPPO_EXE");
            if (!string.IsNullOrEmpty(exe))
            {
                return exe;
            }
            var published = Path.Combine(AppContext.BaseDirectory, "aot", OperatingSystem.IsWindows() ? "hippo.exe" : "hippo");
            if (!File.Exists(published))
            {
                throw new FileNotFoundException(
                    $"No hippo binary at {published}. Building the E2E tests with HIPPO_EXE unset publishes it there; set HIPPO_EXE to test another binary.",
                    published);
            }
            return published;
        }
    }
}
