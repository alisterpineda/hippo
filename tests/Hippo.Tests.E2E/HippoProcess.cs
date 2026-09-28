using System.Diagnostics;

namespace Hippo.Tests.E2E;

/// <summary>
/// Runs hippo as a separate process. Set HIPPO_EXE to the absolute path of a published binary to test that binary;
/// otherwise the build output beside the tests runs under the dotnet host.
/// </summary>
public static class HippoProcess
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    public static Task<Result> RunAsync(params string[] args) => RunAsync(null, new Dictionary<string, string>(), args);

    /// <summary>Runs hippo in <paramref name="workingDirectory"/> (the test's own when null) with
    /// <paramref name="environment"/> added to the inherited environment.</summary>
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
}
