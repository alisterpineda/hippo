using System.Diagnostics;

namespace Hippo.Tests;

/// <summary>
/// A workspace folder and a cache folder under a fresh temp directory, deleted on dispose. The workspace starts with an
/// empty <c>.hippo/config.json</c>. Each test project's subclass adds the way it runs hippo there. <see cref="Git"/>
/// sees no global or system git config, so the user's own ignore rules do not change what a test finds, and does not
/// inherit the test runner's <c>GIT_</c> variables, so a run from a git hook never reaches the outer repository.
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

    /// <summary>Runs git in the workspace and throws when it fails.</summary>
    public void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        RemoveGitVariables(start);
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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: exit {process.ExitCode}: {stderr.Result}");
        }
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

    // Neither file exists; git reads a missing config as empty, and finds no global excludes file under XDG_CONFIG_HOME.
    // Added after RemoveGitVariables, which would remove two of them.
    protected Dictionary<string, string> GitEnvironment => new()
    {
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(Folder, "gitconfig"),
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["XDG_CONFIG_HOME"] = Path.Combine(Folder, "config"),
    };

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
