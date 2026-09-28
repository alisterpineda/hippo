namespace Hippo.Tests.E2E;

/// <summary>
/// A notebook folder and a cache folder under a fresh temp directory, deleted on dispose. hippo runs with the notebook
/// as its working directory and <c>HIPPO_CACHE_DIR</c> pointing at the cache, so no test touches the user's cache.
/// </summary>
public sealed class TempNotebook : IDisposable
{
    private readonly string _dir;

    public TempNotebook()
    {
        // The real path, so it compares equal to the root hippo reports (macOS's temp folder sits behind /var -> /private/var).
        var dir = Path.Combine(Path.GetTempPath(), "hippo-e2e", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _dir = RealPath(dir);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Write(".hippo.yaml", "version: 1\n");
    }

    public string Root => Path.Combine(_dir, "notebook");

    public string CacheDir => Path.Combine(_dir, "cache");

    public string Combine(string relativePath) => Path.Combine(Root, relativePath);

    public string Write(string relativePath, string content)
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Sets every file's mtime a minute into the past, as if written long before, so hippo trusts what it
    /// last read of them.</summary>
    public void Settle()
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, past);
        }
    }

    public Task<HippoProcess.Result> RunAsync(params string[] args) => RunInAsync(Root, args);

    public Task<HippoProcess.Result> RunInAsync(string workingDirectory, params string[] args) =>
        HippoProcess.RunAsync(workingDirectory, new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = CacheDir }, args);

    /// <summary>Runs hippo in the notebook with <paramref name="environment"/> in place of the cache override.</summary>
    public Task<HippoProcess.Result> RunWithAsync(IReadOnlyDictionary<string, string> environment, params string[] args) =>
        HippoProcess.RunAsync(Root, environment, args);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }
        return current;
    }
}
