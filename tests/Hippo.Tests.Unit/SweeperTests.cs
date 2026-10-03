using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
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
        _db.Query<Sweeper.FileRow>("SELECT path, path_nfd AS PathNfd, mtime, size, hash, hashed_at AS HashedAt, kind, frontmatter, parse_error AS ParseError FROM files ORDER BY path").AsList();

    private Sweeper.FileRow Row(string path) => Rows().Single(r => r.Path == path);

    [Fact]
    public void The_first_sweep_adds_every_included_file()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("wiki/b.md", "# B\n");
        _workspace.Write("raw/image.png", "png");

        var result = Sweep();

        Assert.Equal(["a.md", "raw/image.png", "wiki/b.md"], Rows().Select(r => r.Path));
        Assert.Equal(3, result.Added);
        Assert.Equal(3, result.Files);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frontmatter_longer_than_4000_characters_is_stored_whole(bool rebuild)
    {
        // A batch reuses one command, so a short row written first could limit the rows after it. A folder's files are
        // listed before its subfolders', so the short page is written before the long one.
        _workspace.Write("a.md", "---\ntitle: A\n---\n");
        var sources = Enumerable.Range(0, 200).Select(i => $"raw/sources/source-{i:d3}.md").ToList();
        _workspace.Write("wiki/topics/long.md", "---\nsources:\n" + string.Concat(sources.Select(s => $"  - {s}\n")) + "---\n");

        Sweep(rebuild);

        var json = JsonDocument.Parse(Row("wiki/topics/long.md").Frontmatter!);
        Assert.Equal(sources, json.RootElement.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
    }

    [Theory]
    [InlineData(typeof(Sweeper.FileRow))]
    [InlineData(typeof(Sweeper.StatRow))]
    [InlineData(typeof(Sweeper.PathRow))]
    [InlineData(typeof(Sweeper.SourceRow))]
    [InlineData(typeof(Sweeper.LinkRow))]
    [InlineData(typeof(Sweeper.EntryRow))]
    [InlineData(typeof(Sweeper.SearchRow))]
    public void Every_string_of_a_row_written_in_batches_is_unsized(Type row)
    {
        var strings = row.GetProperties().Where(p => p.PropertyType == typeof(string));

        Assert.All(strings, p => Assert.Equal(Sweeper.Unsized, p.GetCustomAttribute<DbValueAttribute>()?.Size));
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
        Assert.Equal(2, result.Added);
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

        Assert.Equal(2, result.Hashed);
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
            Assert.Equal(["locked.md"], SearchRows().Select(r => r.File));
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
        Assert.Equal((count, count), (added.Added, Rows().Count));
        Assert.Equal(count, SearchRows().Count(r => r.File is not null));

        foreach (var path in paths)
        {
            File.WriteAllText(path, File.ReadAllText(path).Replace("n:", "m:"));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        }
        var updated = Sweep();
        Assert.Equal(count, updated.Updated);
        Assert.Equal(count, SearchRows().Count(r => r.File is not null));
        Assert.Equal(count, SearchRows().Count);
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
        Assert.Empty(Rows());
        // Not SearchRows: FTS5 columns have no declared type, so with no row to read Dapper cannot type them.
        Assert.Equal(0, _db.ExecuteScalar<long>("SELECT count(*) FROM search"));
    }

    [Fact]
    public void A_rebuild_of_more_pages_than_one_batch_rewrites_them_all_in_place()
    {
        const int count = 450;
        for (var i = 0; i < count; i++)
        {
            _workspace.Write($"n/{i:d3}.md", $"# Page {i}\n");
        }
        Sweep();
        var ids = _db.Query<(string, long)>("SELECT path, id FROM files ORDER BY path").AsList();

        var result = Sweep(rebuild: true);

        Assert.True(result.Rebuilt);
        Assert.Equal(count, result.Hashed);
        Assert.Equal(ids, _db.Query<(string, long)>("SELECT path, id FROM files ORDER BY path").AsList());
        Assert.Equal(count, SearchRows().Count(r => r.File is not null && r.Title == $"Page {int.Parse(r.Path[2..5], System.Globalization.CultureInfo.InvariantCulture)}"));
        Assert.Equal(count, _db.ExecuteScalar<long>("SELECT count(*) FROM search"));
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
    public void A_sweep_stores_each_path_and_link_target_as_written_and_in_nfd()
    {
        _workspace.Write("caf\u00E9.md", "[x](caf\u00E9-x.md)\n");

        Sweep();

        Assert.Equal(("caf\u00E9.md", "cafe\u0301.md"), (Row("caf\u00E9.md").Path, Row("caf\u00E9.md").PathNfd));
        Assert.Equal(("caf\u00E9-x.md", "cafe\u0301-x.md"),
            _db.QuerySingle<(string, string)>("SELECT target, target_nfd FROM links"));
    }

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
    public void A_link_longer_than_4000_characters_is_stored_whole()
    {
        // After a short link on the same page, so both are written in one batch, the short one first.
        var target = "wiki/" + new string('x', 5000) + ".md";
        _workspace.Write("a.md", $"[b](b.md)\n[long]({target})\n");

        Sweep();

        Assert.Equal([("b.md", "b.md"), (target, target)], Links().Select(l => (l.Raw, l.Target)));
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

        _workspace.Write(".hippo/config.json", """{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "source", "resolve": "bundle" }] } }""");
        var changed = Sweep();
        var after = Sweep();

        Assert.Equal(["b.md"], before.Select(l => l.Target));
        Assert.Equal(["wiki/x.md", "wiki/b.md"], Links().Select(l => l.Target));
        Assert.True(changed.Rebuilt);
        Assert.False(changed.RereadEveryFile);
        Assert.False(after.Rebuilt);
    }

    [Fact]
    public void Changing_the_link_settings_rehashes_only_pages()
    {
        _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("raw/image.png", "png");
        Sweep();

        _workspace.Write(".hippo/config.json", """{ "bundles": ["raw"] }""");
        var changed = Sweep();

        Assert.Equal(1, changed.Hashed);
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

        _workspace.Write(".hippo/config.json", """{ "bundles": ["raw"] }""");
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
            _workspace.Write(".hippo/config.json", """{ "bundles": ["wiki"] }""");
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

    private long FindingCount() => _db.ExecuteScalar<long>("SELECT count(*) FROM findings");

    [Fact]
    public void Declaring_okf_version_relinks_once()
    {
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb"] }""");
        _workspace.Write("kb/index.md", "# KB\n");
        _workspace.Write("kb/a.md", "# No frontmatter\n");
        Sweep();

        _workspace.Write("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# KB\n");
        var declared = Sweep();
        var after = Sweep();

        Assert.True(declared.Rebuilt);
        Assert.Equal(1, FindingCount());
        Assert.False(after.Rebuilt);
        Assert.Equal(0, after.Hashed);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // MakeUnreadable skips the test there.
    public void An_unreadable_root_index_makes_a_plain_bundle_until_readable_and_then_its_findings_come_back()
    {
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb"] }""");
        var index = _workspace.Write("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# KB\n");
        _workspace.Write("kb/a.md", "# No frontmatter\n");
        Sweep();
        Assert.Equal(1, FindingCount());

        MakeUnreadable(index);
        try
        {
            var locked = Sweep();
            Assert.Contains(locked.Warnings, w => w.Contains("kb/index.md"));
            Assert.Empty(locked.OkfBundles);
        }
        finally
        {
            File.SetUnixFileMode(index, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var unlocked = Sweep();

        Assert.Equal(["kb"], unlocked.OkfBundles.Select(b => b.Root));
        Assert.True(unlocked.Rebuilt);
        Assert.Equal(1, FindingCount());
        Assert.False(Sweep().Rebuilt);
    }

    [Fact]
    public void A_page_markdig_cannot_parse_warns_and_the_rest_still_index()
    {
        _workspace.Write("deep.md", new string('>', 200) + " x\n");
        _workspace.Write("a.md", "[b](b.md)\n");

        var result = Sweep();

        Assert.Contains(result.Warnings, w => w.Contains("deep.md"));
        Assert.Equal(["a.md", "deep.md"], Rows().Select(r => r.Path));
        Assert.Equal(["b.md"], Links().Select(l => l.Target));
    }

    [Fact]
    public void Files_lint_exclude_matches_do_not_warn_that_their_frontmatter_or_links_fail_to_parse()
    {
        _workspace.Write(".hippo/config.json", """{ "lint": { "exclude": ["archive/**"] } }""");
        _workspace.Write("archive/bad.md", "---\n- not\n- a mapping\n---\n");
        _workspace.Write("archive/deep.md", new string('>', 200) + " x\n");
        _workspace.Write("notes/bad.md", "---\n- not\n- a mapping\n---\n");
        _workspace.Write("notes/deep.md", new string('>', 200) + " x\n");

        var result = Sweep();

        Assert.Equal(["cannot read the frontmatter in notes/bad.md", "cannot read the links in notes/deep.md"],
            result.Warnings.Select(w => w[..w.IndexOf(':')]).Order(StringComparer.Ordinal));
        Assert.Equal("frontmatter is not a mapping", Row("archive/bad.md").ParseError);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // MakeUnreadable skips the test there.
    public void A_file_lint_exclude_matches_still_warns_when_it_cannot_be_read()
    {
        _workspace.Write(".hippo/config.json", """{ "lint": { "exclude": ["archive/**"] } }""");
        var path = _workspace.Write("archive/locked.md", "# Locked\n");
        MakeUnreadable(path);

        try
        {
            var warning = Assert.Single(Sweep().Warnings);

            Assert.StartsWith("cannot read archive/locked.md: ", warning);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Settings_that_do_not_shape_links_cause_no_rebuild()
    {
        _workspace.Write("a.md", "[b](b.md)\n");
        Sweep();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": ["inbox/**"] } }""");

        Assert.False(Sweep().Rebuilt);
    }

    /// <summary>A row of the search table. <see cref="File"/> is the path of the file whose id is its rowid, or null
    /// when no file has that id.</summary>
    public sealed record StoredSearchRow(string? File, string Title, string Path, string Body);

    private List<StoredSearchRow> SearchRows() =>
        _db.Query<StoredSearchRow>("""
            SELECT f.path AS File, s.title, s.path, s.body
            FROM search s LEFT JOIN files f ON f.id = s.rowid
            ORDER BY s.path, s.rowid
            """).AsList();

    [Fact]
    public void A_sweep_stores_each_pages_title_path_and_body_with_its_whitespace_collapsed_for_search()
    {
        _workspace.Write("wiki/a.md", "---\ntitle: Alpha\n---\n# Heading\n\nThe  body\twraps\r\nhere.\n");
        _workspace.Write("b.md", "# Beta\n");
        _workspace.Write("raw/c.txt", "not a page");

        Sweep();

        Assert.Equal(
            [new StoredSearchRow("b.md", "Beta", "b.md", "# Beta"), new StoredSearchRow("wiki/a.md", "Alpha", "wiki/a.md", "# Heading The body wraps here.")],
            SearchRows());
    }

    [Fact]
    public void Editing_a_page_replaces_its_search_row()
    {
        var path = _workspace.Write("a.md", "# Old\n\nold words\n");
        Sweep();
        File.WriteAllText(path, "# New\n\nnew words\n");

        Sweep();

        Assert.Equal([new StoredSearchRow("a.md", "New", "a.md", "# New new words")], SearchRows());
    }

    [Fact]
    public void Deleting_a_page_drops_its_search_row()
    {
        var path = _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.md", "# B\n");
        Sweep();
        File.Delete(path);

        Sweep();

        Assert.Equal(["b.md"], SearchRows().Select(r => r.File));
    }

    [Fact]
    public void A_rebuild_and_a_relink_keep_one_search_row_per_page()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.md", "# B\n");
        Sweep();

        Sweep(rebuild: true);
        _workspace.Write(".hippo/config.json", """{ "bundles": ["wiki"] }""");
        Assert.True(Sweep().Rebuilt);

        Assert.Equal(["a.md", "b.md"], SearchRows().Select(r => r.File));
    }

    [Fact]
    public void A_relink_leaves_the_search_row_of_an_unchanged_page_as_it_was()
    {
        _workspace.Write("a.md", "# A\n");
        var edited = _workspace.Write("b.md", "# B\n");
        Sweep();
        // A marker no sweep would write, so a rewritten row shows.
        _db.Execute("UPDATE search SET body = 'kept' WHERE path = 'a.md'");
        File.WriteAllText(edited, "# B2\n");

        _workspace.Write(".hippo/config.json", """{ "bundles": ["wiki"] }""");
        Assert.True(Sweep().Rebuilt);

        Assert.Equal(
            [new StoredSearchRow("a.md", "A", "a.md", "kept"), new StoredSearchRow("b.md", "B2", "b.md", "# B2")],
            SearchRows());
    }

    private string SearchTableSql() => _db.ExecuteScalar<string>("SELECT sql FROM sqlite_schema WHERE name = 'search'")!;

    private List<string> Matches(string query) =>
        _db.Query<string>("SELECT path FROM search WHERE search MATCH @query ORDER BY path", new { query }).AsList();

    [Fact]
    public void The_search_table_uses_the_porter_tokenizer_by_default()
    {
        _workspace.Write("a.md", "He runs every morning.\n");

        Sweep();

        Assert.Contains("porter unicode61", SearchTableSql());
        Assert.Equal(["a.md"], Matches("running"));
    }

    [Fact]
    public void Changing_the_tokenizer_rebuilds_the_search_table_keeping_its_rows()
    {
        _workspace.Write("a.md", "# A\n\nThe index of everything.\n");
        _workspace.Write("b.md", "# B\n\nNothing here.\n");
        Sweep();
        var before = SearchRows();
        Assert.Empty(Matches("\"dex\""));

        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        Sweep();

        Assert.Contains("trigram", SearchTableSql());
        Assert.Equal(before, SearchRows());
        Assert.Equal(["a.md"], Matches("\"dex\""));

        _workspace.Write(".hippo/config.json", "{}");
        Sweep();

        Assert.Contains("porter unicode61", SearchTableSql());
        Assert.Equal(before, SearchRows());
        Assert.Empty(Matches("\"dex\""));
    }

    private string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(_workspace.FullPath, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p)
            ? $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {File.ReadAllText(p)}"
            : $"{p}/"));
}
