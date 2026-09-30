using System.Runtime.Versioning;
using Dapper;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

public sealed class SweeperTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _db;

    // An hour ahead, so every file a test writes or touches is settled by the time it is hashed. The racy tests set the
    // clock and the mtimes they need.
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow.AddHours(1));

    public SweeperTests()
    {
        _workspace.Write(".hippo/config.json", """{ "files": { "include": ["**/*"], "exclude": [".git/**", "inbox/**", "**/*.tmp"] } }""");
        _db = _database.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
        _workspace.Dispose();
    }

    private SweepResult Sweep(bool rebuild = false) => Sweeper.Run(Workspace.Open(_workspace.FullPath), _db, rebuild, _clock);

    private void SetMtime(string path, TimeSpan fromNow) => File.SetLastWriteTimeUtc(path, (_clock.Now + fromNow).UtcDateTime);

    private List<Sweeper.FileRow> Rows() =>
        _db.Query<Sweeper.FileRow>("SELECT path, mtime, size, hash, hashed_at AS HashedAt, kind, frontmatter, parse_error AS ParseError FROM files ORDER BY path").AsList();

    private Sweeper.FileRow Row(string path) => Rows().Single(r => r.Path == path);

    [Fact]
    public void The_first_sweep_adds_every_included_file()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("wiki/b.md", "# B\n");
        _workspace.Write("raw/image.png", "png");

        var result = Sweep();

        Assert.Equal([".hippo/config.json", "a.md", "raw/image.png", "wiki/b.md"], Rows().Select(r => r.Path));
        Assert.Equal(4, result.Added);
        Assert.Equal(4, result.Files);
    }

    [Fact]
    public void Markdown_and_other_files_get_their_kind()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.txt", "b");

        Sweep();

        Assert.Equal("markdown", Row("a.md").Kind);
        Assert.Equal("other", Row("b.txt").Kind);
    }

    [Fact]
    public void A_row_records_size_mtime_and_content_hash()
    {
        var path = _workspace.Write("hello.txt", "hello");
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
        _workspace.Write("a.md", "---\ntitle: A\ntype: Topic\n---\n# A\n");

        Sweep();

        Assert.Equal("""{"title":"A","type":"Topic"}""", Row("a.md").Frontmatter);
        Assert.Null(Row("a.md").ParseError);
    }

    [Fact]
    public void Other_files_are_not_parsed_for_frontmatter()
    {
        _workspace.Write("a.txt", "---\ntitle: A\n---\n");

        Sweep();

        Assert.Null(Row("a.txt").Frontmatter);
    }

    [Fact]
    public void Malformed_frontmatter_is_recorded_on_the_row_and_the_sweep_goes_on()
    {
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");
        _workspace.Write("good.md", "---\ntitle: Good\n---\n");

        var result = Sweep();

        Assert.Null(Row("bad.md").Frontmatter);
        Assert.NotNull(Row("bad.md").ParseError);
        Assert.Equal("""{"title":"Good"}""", Row("good.md").Frontmatter);
        Assert.Equal(3, result.Added);
    }

    [Fact]
    public void A_sweep_with_no_changes_reads_no_file()
    {
        _workspace.Write("a.md", "# A\n");
        Sweep();

        var result = Sweep();

        Assert.Equal(0, result.Hashed);
        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Removed));
    }

    [Fact]
    public void A_touched_file_is_rehashed_but_not_updated()
    {
        var path = _workspace.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        var touched = Sweep();
        var after = Sweep();

        Assert.Equal((1, 0), (touched.Hashed, touched.Updated));
        Assert.Equal(0, after.Hashed);
    }

    [Fact]
    public void A_same_size_edit_within_the_mtime_tick_of_the_last_hash_is_caught()
    {
        var path = _workspace.Write("a.md", "---\nt: a\n---\n");
        SetMtime(path, TimeSpan.FromSeconds(-0.5));
        Sweep();
        File.WriteAllText(path, "---\nt: b\n---\n");
        SetMtime(path, TimeSpan.FromSeconds(-0.5));

        var result = Sweep();

        Assert.Equal(1, result.Updated);
        Assert.Equal("""{"t":"b"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_racy_row_is_trusted_once_it_is_rehashed_after_the_margin()
    {
        var path = _workspace.Write("a.md", "---\nt: a\n---\n");
        SetMtime(path, TimeSpan.FromSeconds(-0.5));
        Sweep();
        _clock.Now += TimeSpan.FromSeconds(3);

        var rehashed = Sweep();
        var after = Sweep();

        Assert.Equal((1, 0), (rehashed.Hashed, rehashed.Updated));
        Assert.Equal(0, after.Hashed);
    }

    /// <summary>Writes a.md and hashes it exactly <paramref name="afterMtime"/> after its stored mtime.</summary>
    private string HashAt(TimeSpan afterMtime)
    {
        var path = _workspace.Write("a.md", "---\nt: a\n---\n");
        SetMtime(path, TimeSpan.Zero);
        _clock.Now = new DateTimeOffset(File.GetLastWriteTimeUtc(path)) + afterMtime;
        Sweep();
        return path;
    }

    [Fact]
    public void A_same_size_edit_is_caught_when_the_mtime_is_exactly_the_margin_before_the_hash()
    {
        var path = HashAt(TimeSpan.FromSeconds(2));
        var mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "---\nt: b\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);

        var result = Sweep();

        Assert.Equal(1, result.Updated);
        Assert.Equal("""{"t":"b"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_row_is_trusted_when_the_mtime_is_one_tick_more_than_the_margin_before_the_hash()
    {
        HashAt(TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1));

        Assert.Equal(0, Sweep().Hashed);
    }

    [Fact]
    public void A_row_is_stamped_with_the_time_the_sweep_started()
    {
        // Every clock read is 10 s later than the last, so a row stamped with any read but the first would be trusted
        // and the same-size edit below missed.
        var path = _workspace.Write("a.md", "---\nt: a\n---\n");
        SetMtime(path, TimeSpan.FromSeconds(-0.5));
        var mtime = File.GetLastWriteTimeUtc(path);
        _clock.Step = TimeSpan.FromSeconds(10);
        Sweep();
        File.WriteAllText(path, "---\nt: b\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);

        var result = Sweep();

        Assert.Equal(1, result.Updated);
        Assert.Equal("""{"t":"b"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_future_mtime_is_rehashed_every_sweep_but_not_updated()
    {
        var path = _workspace.Write("a.md", "# A\n");
        SetMtime(path, TimeSpan.FromMinutes(1));
        Sweep();
        var hashedAt = Row("a.md").HashedAt;
        _clock.Now += TimeSpan.FromSeconds(10);

        var result = Sweep();

        Assert.Equal((1, 0), (result.Hashed, result.Updated));
        Assert.Equal(hashedAt, Row("a.md").HashedAt);
    }

    [Fact]
    public void An_unchanged_hash_leaves_the_parsed_frontmatter_alone()
    {
        var path = _workspace.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        _db.Execute("UPDATE files SET frontmatter = '{\"marker\":1}'");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Sweep();

        Assert.Equal("""{"marker":1}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void An_edited_file_is_updated()
    {
        var path = _workspace.Write("a.md", "---\ntitle: A\n---\n");
        Sweep();
        File.WriteAllText(path, "---\ntitle: Changed title\n---\n");

        var result = Sweep();

        Assert.Equal(1, result.Updated);
        Assert.Equal("""{"title":"Changed title"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_deleted_file_is_dropped()
    {
        var path = _workspace.Write("a.md", "# A\n");
        Sweep();
        File.Delete(path);

        var result = Sweep();

        Assert.Equal(1, result.Removed);
        Assert.DoesNotContain(Rows(), r => r.Path == "a.md");
    }

    [Fact]
    public void A_file_newly_excluded_is_dropped()
    {
        _workspace.Write("inbox2/a.md", "# A\n");
        Sweep();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": ["inbox2/**"] } }""");

        Sweep();

        Assert.DoesNotContain(Rows(), r => r.Path == "inbox2/a.md");
    }

    [Fact]
    public void A_rebuild_rereads_and_reparses_every_file()
    {
        _workspace.Write("a.md", "---\ntitle: A\n---\n");
        _workspace.Write("b.txt", "b");
        Sweep();
        _db.Execute("UPDATE files SET frontmatter = '{\"marker\":1}'");

        var result = Sweep(rebuild: true);

        Assert.Equal(3, result.Hashed);
        Assert.True(result.Rebuilt);
        Assert.Equal("""{"title":"A"}""", Row("a.md").Frontmatter);
    }

    [Fact]
    public void A_rebuild_rewrites_each_row_in_place()
    {
        _workspace.Write("a.md", "[b](b.txt)\n");
        _workspace.Write("b.txt", "b");
        Sweep();
        var before = _db.Query<string>("SELECT path || '=' || id FROM files ORDER BY path").AsList();

        Sweep(rebuild: true);

        Assert.Equal(before, _db.Query<string>("SELECT path || '=' || id FROM files ORDER BY path"));
        Assert.Equal(1, _db.ExecuteScalar<long>("SELECT count(*) FROM links"));
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
        var path = _workspace.Write("locked.md", "---\ntitle: Locked\n---\n");
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
        var paths = Enumerable.Range(0, count).Select(i => _workspace.Write($"n/{i:d3}.md", $"---\nn: {i}\n---\n")).ToList();

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
        Assert.Equal([".hippo/config.json"], Rows().Select(r => r.Path));
    }

    [Fact]
    public void Sweeping_never_changes_the_workspace()
    {
        _workspace.Write("a.md", "---\ntitle: A\n---\n");
        _workspace.Write("bad.md", "---\n: [\n---\n");
        _workspace.Write("raw/b.txt", "b");
        var before = Snapshot();

        Sweep();
        Sweep(rebuild: true);

        Assert.Equal(before, Snapshot());
    }

    public sealed record StoredLink(string Source, long Line, string Kind, string Type, string Raw, string? Target);

    private List<StoredLink> Links() =>
        _db.Query<StoredLink>("""
            SELECT f.path AS Source, l.line, l.kind, l.type, l.raw, l.target
            FROM links l JOIN files f ON f.id = l.source_id
            ORDER BY f.path, l.line, l.id
            """).AsList();

    [Fact]
    public void A_sweep_stores_each_pages_links_with_source_line_kind_and_raw_text()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "related[]" }] } }""");
        _workspace.Write("wiki/a.md", "---\nrelated: [c.md]\n---\n\nSee [B](b.md) and <https://example.com>.\n");
        _workspace.Write("wiki/b.md", "[back](#top)\n");
        _workspace.Write("wiki/c.txt", "[not](markdown.md)\n");

        Sweep();

        Assert.Equal(
            [
                new StoredLink("wiki/a.md", 2, "frontmatter", "path", "c.md", "wiki/c.md"),
                new StoredLink("wiki/a.md", 5, "body", "path", "b.md", "wiki/b.md"),
                new StoredLink("wiki/a.md", 5, "body", "url", "https://example.com", null),
                new StoredLink("wiki/b.md", 1, "body", "anchor", "#top", null),
            ],
            Links());
    }

    [Fact]
    public void Editing_a_page_replaces_its_links()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n[c](c.md)\n");
        Sweep();
        File.WriteAllText(path, "[d](d.md)\n");

        Sweep();

        Assert.Equal(["d.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void Deleting_a_page_drops_its_links()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("b.md", "[a](a.md)\n");
        Sweep();
        File.Delete(path);

        Sweep();

        Assert.Equal([new StoredLink("b.md", 1, "body", "path", "a.md", "a.md")], Links());
    }

    [Fact]
    public void A_touched_page_keeps_its_links()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n");
        Sweep();
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Sweep();

        Assert.Equal(["b.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void A_rebuild_keeps_every_link()
    {
        _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("b.md", "[a](a.md)\n");
        Sweep();

        Sweep(rebuild: true);

        Assert.Equal(["b.md", "a.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void Changing_the_link_settings_extracts_every_pages_links_again()
    {
        _workspace.Write("wiki/topics/a.md", "---\nsource: x.md\n---\n[b](/b.md)\n");
        Sweep();
        var before = Links();

        _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["wiki"], "frontmatter": [{ "field": "source", "resolve": "bundle" }] } }""");
        var changed = Sweep();
        var after = Sweep();

        Assert.Equal(["b.md"], before.Select(l => l.Target));
        Assert.Equal(["wiki/x.md", "wiki/b.md"], Links().Select(l => l.Target));
        Assert.True(changed.Rebuilt);
        Assert.False(after.Rebuilt);
    }

    [Fact]
    public void Changing_the_link_settings_rehashes_only_pages()
    {
        _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("raw/image.png", "png");
        Sweep();

        _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["raw"] } }""");
        var changed = Sweep();

        // The page, and the settings file that changed.
        Assert.Equal(2, changed.Hashed);
        Assert.True(changed.Rebuilt);
        Assert.Equal(["b.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void A_relink_records_a_touched_pages_stats_and_keeps_its_id()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n");
        Sweep();
        var before = _db.Query<string>("SELECT path || '=' || id FROM files ORDER BY path").AsList();
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["raw"] } }""");
        var changed = Sweep();
        var after = Sweep();

        Assert.True(changed.Rebuilt);
        Assert.Equal(0, after.Hashed);
        Assert.Equal(before, _db.Query<string>("SELECT path || '=' || id FROM files ORDER BY path"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // MakeUnreadable skips the test there.
    public void A_page_unreadable_when_the_link_settings_change_gets_its_links_under_them_once_readable()
    {
        var path = _workspace.Write("wiki/a.md", "[b](/b.md)\n");
        Sweep();
        MakeUnreadable(path);
        try
        {
            _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["wiki"] } }""");
            var locked = Sweep();
            Assert.Contains(locked.Warnings, w => w.Contains("wiki/a.md"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var unlocked = Sweep();

        Assert.True(unlocked.Rebuilt);
        Assert.Equal(["wiki/b.md"], Links().Select(l => l.Target));
        Assert.False(Sweep().Rebuilt);
    }

    [Fact]
    public void A_page_markdig_cannot_parse_warns_and_the_rest_still_index()
    {
        _workspace.Write("deep.md", new string('>', 200) + " x\n");
        _workspace.Write("a.md", "[b](b.md)\n");

        var result = Sweep();

        Assert.Contains(result.Warnings, w => w.Contains("deep.md"));
        Assert.Equal([".hippo/config.json", "a.md", "deep.md"], Rows().Select(r => r.Path));
        Assert.Equal(["b.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void Settings_that_do_not_shape_links_cause_no_rebuild()
    {
        _workspace.Write("a.md", "[b](b.md)\n");
        Sweep();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": ["inbox/**"] } }""");

        Assert.False(Sweep().Rebuilt);
    }

    private string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(_workspace.FullPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p)
            ? $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {File.ReadAllText(p)}"
            : $"{p}/"));
}
