using System.Runtime.Versioning;
using Dapper;
using Hippo.Indexing;
using Hippo.Notebooks;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

public sealed class SweeperTests : IDisposable
{
    private readonly TempDirectory _notebook = new();
    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _db;

    public SweeperTests()
    {
        _notebook.Write(".hippo.yaml", "version: 1\nfiles:\n  include: [\"**/*\"]\n  exclude: [\".git/**\", \"inbox/**\", \"**/*.tmp\"]\n");
        _db = _database.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
        _notebook.Dispose();
    }

    private SweepResult Sweep(bool rebuild = false) => Sweeper.Run(Notebook.Open(_notebook.FullPath), _db, rebuild);

    private List<Sweeper.FileRow> Rows() =>
        _db.Query<Sweeper.FileRow>("SELECT path, mtime, size, hash, kind, frontmatter, parse_error AS ParseError FROM files ORDER BY path").AsList();

    private Sweeper.FileRow Row(string path) => Rows().Single(r => r.Path == path);

    [Fact]
    public void The_first_sweep_adds_every_included_file()
    {
        _notebook.Write("a.md", "# A\n");
        _notebook.Write("wiki/b.md", "# B\n");
        _notebook.Write("raw/image.png", "png");

        var result = Sweep();

        Assert.Equal([".hippo.yaml", "a.md", "raw/image.png", "wiki/b.md"], Rows().Select(r => r.Path));
        Assert.Equal(4, result.Added);
        Assert.Equal(4, result.Files);
    }

    [Fact]
    public void Excluded_files_and_folders_are_not_indexed()
    {
        _notebook.Write("a.md", "# A\n");
        _notebook.Write(".git/HEAD", "ref: refs/heads/main\n");
        _notebook.Write("inbox/new.md", "# New\n");
        _notebook.Write("wiki/scratch.tmp", "x");

        Sweep();

        Assert.Equal([".hippo.yaml", "a.md"], Rows().Select(r => r.Path));
    }

    [Fact]
    public void Only_included_files_are_indexed()
    {
        _notebook.Write(".hippo.yaml", "files:\n  include: [\"wiki/**/*.md\"]\n");
        _notebook.Write("wiki/a.md", "# A\n");
        _notebook.Write("wiki/deep/b.md", "# B\n");
        _notebook.Write("wiki/c.txt", "c");
        _notebook.Write("raw/d.md", "# D\n");

        Sweep();

        Assert.Equal(["wiki/a.md", "wiki/deep/b.md"], Rows().Select(r => r.Path));
    }

    [Fact]
    public void Markdown_and_plain_files_get_their_kind()
    {
        _notebook.Write("a.md", "# A\n");
        _notebook.Write("b.txt", "b");

        Sweep();

        Assert.Equal("markdown", Row("a.md").Kind);
        Assert.Equal("plain", Row("b.txt").Kind);
    }

    [Fact]
    public void A_row_records_size_mtime_and_content_hash()
    {
        var path = _notebook.Write("hello.txt", "hello");
        var mtime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, mtime);

        Sweep();

        var row = Row("hello.txt");
        Assert.Equal(5, row.Size);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", row.Hash);
        Assert.Equal((mtime - DateTime.UnixEpoch).Ticks, row.Mtime);
    }

    [Fact]
    public void Frontmatter_is_stored_as_json()
    {
        _notebook.Write("a.md", "---\ntitle: A\ntype: Topic\n---\n# A\n");

        Sweep();

        Assert.Equal("""{"title":"A","type":"Topic"}""", Row("a.md").Frontmatter);
        Assert.Null(Row("a.md").ParseError);
    }

    [Fact]
    public void Plain_files_are_not_parsed_for_frontmatter()
    {
        _notebook.Write("a.txt", "---\ntitle: A\n---\n");

        Sweep();

        Assert.Null(Row("a.txt").Frontmatter);
    }

    [Fact]
    public void Malformed_frontmatter_is_recorded_on_the_row_and_the_sweep_goes_on()
    {
        _notebook.Write("bad.md", "---\ntitle: [unclosed\n---\n");
        _notebook.Write("good.md", "---\ntitle: Good\n---\n");

        var result = Sweep();

        Assert.Null(Row("bad.md").Frontmatter);
        Assert.NotNull(Row("bad.md").ParseError);
        Assert.Equal("""{"title":"Good"}""", Row("good.md").Frontmatter);
        Assert.Equal(3, result.Added);
    }

    [Fact]
    public void A_sweep_with_no_changes_reads_no_file()
    {
        _notebook.Write("a.md", "# A\n");
        Sweep();

        var result = Sweep();

        Assert.Equal(0, result.Hashed);
        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Removed));
    }

    [Fact]
    public void A_touched_file_is_rehashed_but_not_updated()
    {
        var path = _notebook.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        var touched = Sweep();
        var after = Sweep();

        Assert.Equal((1, 0), (touched.Hashed, touched.Updated));
        Assert.Equal(0, after.Hashed);
    }

    [Fact]
    public void An_unchanged_hash_leaves_the_parsed_frontmatter_alone()
    {
        var path = _notebook.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        _db.Execute("UPDATE files SET frontmatter = '{\"marker\":1}'");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Sweep();

        Assert.Equal("""{"marker":1}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void An_edited_file_is_updated()
    {
        var path = _notebook.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        File.WriteAllText(path, "---\ntitle: Changed title\n---\n");

        var result = Sweep();

        Assert.Equal(1, result.Updated);
        Assert.Equal("""{"title":"Changed title"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_deleted_file_is_dropped()
    {
        var path = _notebook.Write("a.md", "# A\n");
        Sweep();
        File.Delete(path);

        var result = Sweep();

        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(Rows(), r => r.Path == "a.md");
    }

    [Fact]
    public void A_file_newly_excluded_is_dropped()
    {
        _notebook.Write("inbox2/a.md", "# A\n");
        Sweep();
        _notebook.Write(".hippo.yaml", "files:\n  exclude: [\"inbox2/**\"]\n");

        Sweep();

        Assert.DoesNotContain(Rows(), r => r.Path == "inbox2/a.md");
    }

    [Fact]
    public void A_rebuild_rereads_and_reparses_every_file()
    {
        _notebook.Write("a.md", "---\ntitle: A\n---\n");
        _notebook.Write("b.txt", "b");
        Sweep();
        _db.Execute("UPDATE files SET frontmatter = '{\"marker\":1}'");

        var result = Sweep(rebuild: true);

        Assert.Equal(3, result.Hashed);
        Assert.True(result.Rebuilt);
        Assert.Equal("""{"title":"A"}""", Row("a.md").Frontmatter);
    }

    /// <summary>Makes <paramref name="path"/> unreadable, or skips the test where permissions cannot do that
    /// (Windows, or running as root).</summary>
    [UnsupportedOSPlatform("windows")]
    private static void MakeUnreadable(string path)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.OpenRead(path).Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Skip("this user can read a file with no permissions");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [UnsupportedOSPlatform("windows")] // MakeUnreadable skips the test there.
    public void An_unreadable_file_keeps_its_row_and_warns(bool rebuild)
    {
        var path = _notebook.Write("locked.md", "---\ntitle: Locked\n---\n");
        Sweep();
        MakeUnreadable(path);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        try
        {
            var result = Sweep(rebuild);

            Assert.Equal("""{"title":"Locked"}""", Row("locked.md").Frontmatter);
            Assert.Equal(0, result.Removed);
            Assert.Contains(result.Warnings, w => w.Contains("locked.md"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Changes_to_more_files_than_one_batch_are_all_written()
    {
        const int count = 450;
        var paths = Enumerable.Range(0, count).Select(i => _notebook.Write($"n/{i:d3}.md", $"---\nn: {i}\n---\n")).ToList();

        var added = Sweep();
        Assert.Equal((count + 1, count + 1), (added.Added, Rows().Count));

        foreach (var path in paths)
        {
            File.WriteAllText(path, File.ReadAllText(path).Replace("n:", "m:"));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        }
        var updated = Sweep();
        Assert.Equal(count, updated.Updated);
        Assert.Equal(count, Rows().Count(r => r.Frontmatter?.StartsWith("""{"m":""", StringComparison.Ordinal) == true));

        foreach (var path in paths)
        {
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        }
        var touched = Sweep();
        var after = Sweep();
        Assert.Equal((count, 0), (touched.Hashed, after.Hashed));

        foreach (var path in paths)
        {
            File.Delete(path);
        }
        var removed = Sweep();
        Assert.Equal(count, removed.Removed);
        Assert.Equal([".hippo.yaml"], Rows().Select(r => r.Path));
    }

    [Fact]
    public void A_backslash_in_a_file_name_is_kept_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a backslash separates folders on Windows");
        _notebook.Write("a\\b.md", "# A\n");

        Sweep();

        Assert.Contains("a\\b.md", Rows().Select(r => r.Path));
    }

    [Fact]
    public void Symbolic_links_are_not_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs privileges on Windows");
        using var outside = new TempDirectory();
        outside.Write("secret.md", "# Outside\n");
        Directory.CreateSymbolicLink(_notebook.Combine("linked"), outside.FullPath);
        File.CreateSymbolicLink(_notebook.Combine("link.md"), outside.Combine("secret.md"));

        Sweep();

        Assert.Equal([".hippo.yaml"], Rows().Select(r => r.Path));
    }

    [Fact]
    public void Sweeping_never_changes_the_notebook()
    {
        _notebook.Write("a.md", "---\ntitle: A\n---\n");
        _notebook.Write("bad.md", "---\n: [\n---\n");
        _notebook.Write("raw/b.txt", "b");
        var before = Snapshot();

        Sweep();
        Sweep(rebuild: true);

        Assert.Equal(before, Snapshot());
    }

    private string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(_notebook.FullPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p)
            ? $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {File.ReadAllText(p)}"
            : $"{p}/"));
}
