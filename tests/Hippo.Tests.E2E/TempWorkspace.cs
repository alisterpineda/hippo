using System.Diagnostics;

namespace Hippo.Tests.E2E;

/// <summary>
/// A <see cref="WorkspaceFixture"/> where hippo runs as a process, with the workspace as its working directory and
/// <c>HIPPO_CACHE_DIR</c> pointing at the cache, so no test touches the user's cache. hippo and <see cref="Git"/> see no
/// global or system git config, so the user's own ignore rules do not change what a test finds, and neither inherits the
/// test runner's <c>GIT_</c> variables, so a run from a git hook never reaches the outer repository.
/// </summary>
public sealed class TempWorkspace : WorkspaceFixture
{
    public Task<HippoProcess.Result> RunAsync(params string[] args) => RunInAsync(Root, args);

    public Task<HippoProcess.Result> RunInAsync(string workingDirectory, params string[] args) =>
        HippoProcess.RunAsync(workingDirectory, new Dictionary<string, string>(GitEnvironment) { ["HIPPO_CACHE_DIR"] = CacheDir }, args);

    /// <summary>Runs hippo in the workspace with <paramref name="environment"/> in place of the cache override, over the
    /// git isolation.</summary>
    public Task<HippoProcess.Result> RunWithAsync(IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var merged = GitEnvironment;
        foreach (var (name, value) in environment)
        {
            merged[name] = value;
        }
        return HippoProcess.RunAsync(Root, merged, args);
    }

    /// <summary>Runs git in the workspace and fails the test when it fails.</summary>
    public void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        HippoProcess.RemoveGitVariables(start);
        foreach (var (name, value) in GitEnvironment)
        {
            start.Environment[name] = value;
        }
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: exit {process.ExitCode}: {stderr.Result}");
    }

    // Neither file exists; git reads a missing config as empty, and finds no global excludes file under XDG_CONFIG_HOME.
    private Dictionary<string, string> GitEnvironment => new()
    {
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(Folder, "gitconfig"),
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["XDG_CONFIG_HOME"] = Path.Combine(Folder, "config"),
    };
}
