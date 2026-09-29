using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

/// <summary>Which files a workspace holds. git's part is in the E2E <c>GitignoreTests</c>, which run a real git.</summary>
public sealed class WorkspaceFilesTests : IDisposable
{
    private readonly TempDirectory _workspace = new();

    public WorkspaceFilesTests() =>
        _workspace.Write(".hippo/config.json", """{ "files": { "include": ["**/*"], "exclude": [".git/**", "inbox/**", "**/*.tmp"] } }""");

    public void Dispose() => _workspace.Dispose();

    private Workspace Open() => Workspace.Open(_workspace.FullPath);

    private List<string> Paths() => Open().ListFiles([]).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Excluded_files_and_folders_are_left_out()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write(".git/HEAD", "ref: refs/heads/main\n");
        _workspace.Write("inbox/new.md", "# New\n");
        _workspace.Write("wiki/scratch.tmp", "x");

        Assert.Equal([".hippo/config.json", "a.md"], Paths());
    }

    [Fact]
    public void Only_included_files_are_listed()
    {
        _workspace.Write(".hippo/config.json", """{ "files": { "include": ["wiki/**/*.md"] } }""");
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("wiki/deep/b.md", "# B\n");
        _workspace.Write("wiki/c.txt", "c");
        _workspace.Write("raw/d.md", "# D\n");

        Assert.Equal(["wiki/a.md", "wiki/deep/b.md"], Paths());
    }

    [Fact]
    public void A_file_has_its_full_path_size_and_mtime()
    {
        var path = _workspace.Write(Path.Combine("wiki", "a.md"), "# A\n");
        var mtime = new DateTimeOffset(2026, 9, 1, 12, 30, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(path, mtime.UtcDateTime);

        var file = Open().ListFiles([]).Single(f => f.Path == "wiki/a.md");

        Assert.Equal(new WorkspaceFile("wiki/a.md", path, 4, mtime), file);
    }

    [Fact]
    public void A_backslash_in_a_file_name_is_kept_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a backslash separates folders on Windows");
        _workspace.Write("a\\b.md", "# A\n");

        Assert.Contains("a\\b.md", Paths());
    }

    [Fact]
    public void Symbolic_links_are_not_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs privileges on Windows");
        using var outside = new TempDirectory();
        outside.Write("secret.md", "# Outside\n");
        Directory.CreateSymbolicLink(_workspace.Combine("linked"), outside.FullPath);
        File.CreateSymbolicLink(_workspace.Combine("link.md"), outside.Combine("secret.md"));

        Assert.Equal([".hippo/config.json"], Paths());
    }

    [Fact]
    public void A_glob_matches_keys_relative_to_the_root()
    {
        var matched = Open().Glob("wiki/**/*.md", ["a.md", "wiki/b.md", "wiki/deep/c.md", "wiki/d.txt"]);

        Assert.Equal(["wiki/b.md", "wiki/deep/c.md"], matched.Order(StringComparer.Ordinal));
    }
}
