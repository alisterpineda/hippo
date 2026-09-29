using System.Text.Json;

namespace Hippo.Tests.E2E;

/// <summary>
/// What only the real process shows: the user cache folder, found from the process's own environment; exit codes as the
/// shell sees them; and control characters passed as real arguments and printed to real output. Everything else about
/// the commands is tested in-process, in <c>Hippo.Tests.Integration</c>.
/// </summary>
public sealed class CommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(HippoProcess.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task Without_hippo_cache_dir_the_index_is_in_the_user_cache_folder()
    {
        // The cache folder stands in for the user's home, so nothing lands in the real one.
        var home = _workspace.CacheDir;
        var json = Json(await _workspace.RunWithAsync(
            new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = "", ["XDG_CACHE_HOME"] = "", ["HOME"] = home, ["LOCALAPPDATA"] = home },
            "status", "--json"));

        var expected = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "hippo")
            : OperatingSystem.IsWindows() ? Path.Combine(home, "hippo")
            : Path.Combine(home, ".cache", "hippo");
        var database = json.GetProperty("database").GetString()!;
        Assert.Equal(expected, Path.GetDirectoryName(Path.GetDirectoryName(database)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(database)!));
        }
    }

    [Fact]
    public async Task Control_characters_in_file_names_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        _workspace.Write("n\u001b]0;T\u0007.md", "# N\n");

        var result = await _workspace.RunAsync("files");

        Assert.Contains("n\\x1b]0;T\\x07.md", Lines(result.Stdout));
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public async Task Control_characters_in_the_status_paths_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        var cache = Path.Combine(_workspace.CacheDir, "c\u001b]0;T\u0007");

        var result = await _workspace.RunWithAsync(new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = cache }, "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout),
            l => l.StartsWith($"Database:    {_workspace.CacheDir}{Path.DirectorySeparatorChar}c\\x1b]0;T\\x07", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public async Task Control_characters_in_the_status_workspace_root_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        _workspace.Write("w\u001b]0;T\u0007/.hippo/config.json", "");

        var result = await _workspace.RunInAsync(_workspace.Combine("w\u001b]0;T\u0007"), "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout),
            l => l == $"Workspace:   {_workspace.Root}{Path.DirectorySeparatorChar}w\\x1b]0;T\\x07");
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public async Task Control_characters_in_an_error_are_escaped()
    {
        var result = await _workspace.RunAsync("show", "n\u001b]0;T\u0007.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hippo: n\\x1b]0;T\\x07.md is not in the index", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
    }

    [Fact]
    public async Task A_usage_error_exits_2()
    {
        var result = await _workspace.RunAsync("files", "--bogus");

        Assert.Equal(2, result.ExitCode);
    }
}
