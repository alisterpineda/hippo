using System.CommandLine;

namespace Hippo.Tests.Integration;

/// <summary>
/// A workspace folder and a cache folder under a fresh temp directory, deleted on dispose. hippo's commands run in this
/// process, reading the working directory, environment and clock from a <see cref="CliEnvironment"/> rather than the
/// test process's own: the workspace as the working directory, and only <c>HIPPO_CACHE_DIR</c> set, pointing at the
/// cache. The workspace is outside any repository, so hippo never runs git, which would see the test process's own
/// environment; tests that need git are E2E tests.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    private readonly TempDirectory _dir = new();

    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Write(".hippo/config.json", "");
    }

    public string Root => _dir.Combine("workspace");

    public string CacheDir => _dir.Combine("cache");

    /// <summary>The clock hippo reads: the real one unless a test sets its own.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The mount points hippo reads: only the root of the temp directory's drive unless a test sets its own,
    /// so what a test sees does not depend on what this machine has mounted.</summary>
    public IReadOnlyList<string> MountPoints { get; set; } = [Path.GetPathRoot(Path.GetTempPath())!];

    public string Combine(string relativePath) => Path.Combine(Root, relativePath);

    /// <summary>A path in the temp directory beside the workspace and the cache.</summary>
    public string Beside(string relativePath) => _dir.Combine(relativePath);

    /// <summary>Makes another workspace at <paramref name="relativePath"/> beside this one; run hippo there with
    /// <see cref="RunIn"/>, and it shares this cache.</summary>
    public string AddWorkspace(string relativePath)
    {
        var root = Beside(relativePath);
        _dir.Write(Path.Combine(relativePath, ".hippo", "config.json"), "");
        return root;
    }

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

    public Result Run(params string[] args) => RunIn(Root, args);

    public Result RunIn(string workingDirectory, params string[] args) =>
        Invoke(workingDirectory, new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = CacheDir }, args);

    /// <summary>Runs hippo in the workspace with <paramref name="environment"/> as its only variables, in place of the
    /// cache override.</summary>
    public Result RunWith(IReadOnlyDictionary<string, string> environment, params string[] args) => Invoke(Root, environment, args);

    private Result Invoke(string workingDirectory, IReadOnlyDictionary<string, string> variables, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = new CliEnvironment(workingDirectory, name => variables.GetValueOrDefault(name), Clock, () => MountPoints);

        var exitCode = Cli.Run(args, environment, new InvocationConfiguration { Output = output, Error = error });

        return new Result(exitCode, output.ToString(), error.ToString());
    }

    public void Dispose() => _dir.Dispose();
}
