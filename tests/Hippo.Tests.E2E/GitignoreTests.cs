using System.Runtime.Versioning;

namespace Hippo.Tests.E2E;

public sealed class GitignoreTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void WriteIgnoredTree()
    {
        _workspace.Git("init");
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"] } }""");
        _workspace.Write(".gitignore", "build/\n*.log\n");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("build/out.md", "# Out\n");
        _workspace.Write("build/deep/more.md", "# More\n");
        _workspace.Write("notes/b.md", "# B\n");
        _workspace.Write("notes/debug.log", "log");
        _workspace.Write("only-logs/x.log", "log");
    }

    [Fact]
    public async Task Files_git_ignores_are_not_listed()
    {
        WriteIgnoredTree();

        var files = await _workspace.RunAsync("find");

        Assert.True(files.ExitCode == 0, $"exit {files.ExitCode}: {files.Stderr}");
        Assert.Equal("", files.Stderr);
        Assert.Equal([".gitignore", "a.md", "notes/b.md"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_tracked_file_is_listed_though_it_matches_an_ignore_rule()
    {
        WriteIgnoredTree();
        _workspace.Git("add", "--force", "build/out.md");

        var files = await _workspace.RunAsync("find");

        Assert.Contains("build/out.md", Lines(files.Stdout));
        Assert.DoesNotContain("build/deep/more.md", Lines(files.Stdout));
    }

    [Fact]
    public async Task Turning_gitignore_off_lists_ignored_files()
    {
        WriteIgnoredTree();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"], "gitignore": false } }""");

        var files = await _workspace.RunAsync("find");

        Assert.Equal(
            [".gitignore", "a.md", "build/deep/more.md", "build/out.md", "notes/b.md", "notes/debug.log", "only-logs/x.log"],
            Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_file_that_becomes_ignored_leaves_the_index()
    {
        _workspace.Git("init");
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"] } }""");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("draft.md", "# Draft\n");
        await _workspace.RunAsync("index");

        _workspace.Write(".gitignore", "draft.md\n");
        var files = await _workspace.RunAsync("find");

        Assert.DoesNotContain("draft.md", Lines(files.Stdout));
        Assert.Contains("a.md", Lines(files.Stdout));
    }

    [Fact]
    public async Task A_workspace_below_the_repository_root_follows_the_repositorys_rules()
    {
        _workspace.Git("init");
        _workspace.Write(".gitignore", "/vault/drafts/\n");
        _workspace.Write("vault/.hippo/config.json", "");
        _workspace.Write("vault/a.md", "# A\n");
        _workspace.Write("vault/drafts/b.md", "# B\n");

        var files = await _workspace.RunInAsync(_workspace.Combine("vault"), "find");

        Assert.Equal("", files.Stderr);
        Assert.Equal(["a.md"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/vault/")]
    [InlineData("vault/*")]
    [InlineData("vault/**")]
    public async Task A_workspace_the_repository_ignores_leaves_nothing_out(string rule)
    {
        _workspace.Git("init");
        _workspace.Write(".gitignore", rule + "\n");
        _workspace.Write("vault/.hippo/config.json", "");
        _workspace.Write("vault/a.md", "# A\n");
        _workspace.Write("vault/s/b.md", "# B\n");

        var files = await _workspace.RunInAsync(_workspace.Combine("vault"), "find");

        Assert.Equal("", files.Stderr);
        Assert.Equal(["a.md", "s/b.md"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Outside_a_repository_git_is_not_run_and_nothing_is_warned()
    {
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");

        var files = await _workspace.RunAsync("find");

        Assert.Equal("", files.Stderr);
        Assert.Equal([".gitignore", "debug.log"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_git_file_standing_for_the_repository_counts_as_one()
    {
        _workspace.Git("init", "--separate-git-dir", Path.Combine(_workspace.CacheDir, "repo.git"));
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git"] } }""");
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");

        var files = await _workspace.RunAsync("find");

        Assert.True(File.Exists(_workspace.Combine(".git")));
        Assert.Equal("", files.Stderr);
        Assert.Equal([".gitignore"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task A_git_in_the_workspace_is_never_run()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the planted git is a shell script");
        _workspace.Git("init");
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"] } }""");
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");
        var marker = Path.Combine(_workspace.CacheDir, "planted-git-ran");
        var planted = _workspace.Write("git", $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var files = await _workspace.RunAsync("find");

        Assert.False(File.Exists(marker), "hippo ran the git in the workspace");
        Assert.Equal("", files.Stderr);
        Assert.DoesNotContain("debug.log", Lines(files.Stdout));
    }

    [Fact]
    public async Task A_repository_config_cannot_make_hippo_run_its_fsmonitor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the monitor is a POSIX shell command");
        _workspace.Git("init");
        var marker = Path.Combine(_workspace.CacheDir, "fsmonitor-ran");
        // git runs a core.fsmonitor that is not a boolean through the shell.
        _workspace.Git("config", "core.fsmonitor", $"touch '{marker}'");

        var files = await _workspace.RunAsync("find");

        Assert.Equal(0, files.ExitCode);
        Assert.False(File.Exists(marker), "hippo let git run the repository's core.fsmonitor");
    }

    [Fact]
    public async Task More_ignored_files_than_a_pipe_holds_are_all_left_out()
    {
        _workspace.Git("init");
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"] } }""");
        _workspace.Write(".gitignore", "*.log\n");
        // Listed one by one, as the root also holds files git does not ignore: well over a 64 KB pipe buffer.
        for (var i = 0; i < 2000; i++)
        {
            _workspace.Write($"an-ignored-file-with-a-rather-long-name-{i:D5}.log", "");
        }

        var files = await _workspace.RunAsync("find");

        Assert.Equal("", files.Stderr);
        Assert.Equal([".gitignore"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_broken_repository_warns_and_ignores_nothing()
    {
        _workspace.Write(".git/HEAD", "ref: refs/heads/main\n");
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");

        var files = await _workspace.RunAsync("find");

        Assert.Equal(0, files.ExitCode);
        Assert.Contains("hippo: warning: git could not list the files it ignores, so they are indexed:", files.Stderr);
        Assert.Contains("debug.log", Lines(files.Stdout));
    }

    [Fact]
    public async Task Without_git_on_the_path_hippo_warns_and_ignores_nothing()
    {
        _workspace.Git("init");
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");
        var empty = Directory.CreateDirectory(Path.Combine(_workspace.CacheDir, "empty-path")).FullName;

        var files = await _workspace.RunWithAsync(
            new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = _workspace.CacheDir, ["PATH"] = empty }, "find");

        Assert.Equal(0, files.ExitCode);
        Assert.Contains("hippo: warning: cannot run git, so the files it ignores are indexed", files.Stderr);
        Assert.Contains("debug.log", Lines(files.Stdout));
    }
}
