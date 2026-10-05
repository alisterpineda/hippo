using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

/// <summary>Which files a workspace holds. git's part is in the E2E <c>FindCommandTests</c>, under the Gitignore topic, which run a real git.</summary>
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

        Assert.Equal(["a.md"], Paths());
    }

    [Fact]
    public void The_roots_hippo_folder_is_left_out_whatever_include_says_but_a_nested_workspaces_is_not()
    {
        _workspace.Write(".hippo/config.json", """{ "files": { "include": ["**/*", ".hippo/**"] } }""");
        _workspace.Write(".hippo/other.json", "{}");
        _workspace.Write("vault/.hippo/config.json", "");

        Assert.Equal(["vault/.hippo/config.json"], Paths());
    }

    [Fact]
    public void The_roots_hippo_folder_is_left_out_under_another_casing_where_the_filesystem_ignores_case()
    {
        Directory.Move(_workspace.Combine(".hippo"), _workspace.Combine(".Hippo"));
        Assert.SkipUnless(Directory.Exists(_workspace.Combine(".hippo")), "the filesystem tells case apart");
        _workspace.Write("a.md", "# A\n");

        Assert.Equal(["a.md"], Paths());
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

        Assert.Empty(Paths());
    }

    [Fact]
    public void A_glob_matches_keys_relative_to_the_root()
    {
        var matched = Open().Glob(["wiki/**/*.md"], ["a.md", "wiki/b.md", "wiki/deep/c.md", "wiki/d.txt"]);

        Assert.Equal(["wiki/b.md", "wiki/deep/c.md"], matched.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_key_any_of_several_globs_matches_is_matched()
    {
        var matched = Open().Glob(["wiki/*.md", "*.txt"], ["a.md", "b.txt", "wiki/c.md", "wiki/d.txt"]);

        Assert.Equal(["b.txt", "wiki/c.md"], matched.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_glob_starting_with_an_exclamation_mark_leaves_out_what_it_matches()
    {
        var matched = Open().Glob(["wiki/**", "!wiki/drafts/**"], ["a.md", "wiki/b.md", "wiki/drafts/c.md"]);

        Assert.Equal(["wiki/b.md"], matched.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_glob_in_either_form_matches_both_keys_equal_to_it_under_nfc()
    {
        // Two keys equal under NFC, as a filesystem that keeps names apart by form can hold.
        string[] keys = ["caf\u00E9.md", "cafe\u0301.md", "other.md"];

        Assert.Equal(["cafe\u0301.md", "caf\u00E9.md"], Open().Glob(["caf\u00E9.md"], keys).Order(StringComparer.Ordinal));
        Assert.Equal(["cafe\u0301.md", "caf\u00E9.md"], Open().Glob(["cafe\u0301.md"], keys).Order(StringComparer.Ordinal));
        Assert.Equal(["other.md"], Open().Glob(["!caf\u00E9.md"], keys).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_glob_in_nfc_matches_a_key_in_nfd()
    {
        Assert.Equal(["wiki/cafe\u0301.md"], Open().Glob(["wiki/caf\u00E9.md"], ["wiki/cafe\u0301.md", "wiki/b.md"]));
        Assert.Equal(["wiki/b.md"], Open().Glob(["wiki/**", "!wiki/caf\u00E9.md"], ["wiki/cafe\u0301.md", "wiki/b.md"]));
    }

    [Fact]
    public void Globs_that_all_exclude_leave_out_what_they_match_from_every_key()
    {
        var matched = Open().Glob(["!archive/**", "!*.png"], ["a.md", "b.png", "archive/c.md", "wiki/d.md", "wiki/e.png"]);

        Assert.Equal(["a.md", "wiki/d.md", "wiki/e.png"], matched.Order(StringComparer.Ordinal));
    }
}
