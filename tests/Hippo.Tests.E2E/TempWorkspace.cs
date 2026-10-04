namespace Hippo.Tests.E2E;

/// <summary>
/// A <see cref="WorkspaceFixture"/> where hippo runs as a process, with the workspace as its working directory and
/// <c>HIPPO_CACHE_DIR</c> pointing at the cache, so no test touches the user's cache. hippo, and git through it, get
/// the same isolation as <see cref="WorkspaceFixture.Git"/>: no global or system git config, and none of the test
/// runner's <c>GIT_</c> variables.
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
}
