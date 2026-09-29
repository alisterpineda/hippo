using System.Diagnostics;

namespace Hippo.Tests.E2E;

/// <summary>
/// A workspace folder and a cache folder under a fresh temp directory, deleted on dispose. hippo runs with the workspace
/// as its working directory and <c>HIPPO_CACHE_DIR</c> pointing at the cache, so no test touches the user's cache. hippo
/// and <see cref="Git"/> see no global or system git config, so the user's own ignore rules do not change what a test
/// finds.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private readonly string _dir;

    public TempWorkspace()
    {
        // The real path, so it compares equal to the root hippo reports (macOS's temp folder sits behind /var -> /private/var).
        var dir = Path.Combine(Path.GetTempPath(), "hippo-e2e", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _dir = RealPath(dir);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Write(".hippo/config.json", "");
    }

    public string Root => Path.Combine(_dir, "workspace");

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
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(_dir, "gitconfig"),
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["XDG_CONFIG_HOME"] = Path.Combine(_dir, "config"),
    };

    public void Dispose()
    {
        try
        {
            // git writes its objects read-only, and Windows will not delete a read-only file.
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
            }
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
