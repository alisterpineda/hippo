namespace Hippo.Tests;

/// <summary>
/// A workspace folder and a cache folder under a fresh temp directory, deleted on dispose. The workspace starts with an
/// empty <c>.hippo/config.json</c>. Each test project's subclass adds the way it runs hippo there.
/// </summary>
public abstract class WorkspaceFixture : IDisposable
{
    private readonly TempDirectory _dir = new();

    protected WorkspaceFixture()
    {
        // The real path, so it compares equal to the root hippo reports (macOS's temp folder sits behind /var -> /private/var).
        Folder = RealPath(_dir.FullPath);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Write(".hippo/config.json", "");
    }

    public string Root => Path.Combine(Folder, "workspace");

    public string CacheDir => Path.Combine(Folder, "cache");

    /// <summary>The temp directory that holds the workspace and the cache.</summary>
    protected string Folder { get; }

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

    public void Dispose() => _dir.Dispose();

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
