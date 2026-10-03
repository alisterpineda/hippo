using System.Globalization;
using Dapper;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

public class MigrationRunnerTests
{
    [Fact]
    public void A_new_database_gets_every_script_and_the_latest_version()
    {
        using var db = new TestDatabase();
        using var connection = db.Connect();

        var applied = MigrationRunner.Migrate(connection);

        Assert.Equal(MigrationRunner.Scripts.Count, applied);
        Assert.Equal(MigrationRunner.LatestVersion, connection.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'files'"));
    }

    [Fact]
    public void The_database_is_left_in_wal_mode_with_foreign_keys_on()
    {
        using var db = new TestDatabase();
        using var connection = db.Connect();

        MigrationRunner.Migrate(connection);

        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(30000, connection.ExecuteScalar<long>("PRAGMA busy_timeout"));
        Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public void A_current_database_gets_nothing()
    {
        using var db = new TestDatabase();
        using (var first = db.Connect())
        {
            MigrationRunner.Migrate(first);
        }
        using var connection = db.Connect();

        Assert.Equal(0, MigrationRunner.Migrate(connection));
    }

    /// <summary>
    /// A migration keeps what the index holds: every script after the first must leave each row it finds, with the same
    /// id and the same values in the first schema's columns. Columns a later script adds are not seeded or compared. A
    /// migration meant to discard rows changes this test in the same commit.
    /// </summary>
    [Fact]
    public void Migrating_a_populated_index_keeps_every_row()
    {
        using var db = new TestDatabase();
        using (var first = db.Connect())
        {
            first.Execute(MigrationRunner.Scripts[0].Sql);
            first.Execute("PRAGMA user_version = 1");
            first.Execute("""
                INSERT INTO files (id, path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error) VALUES
                    (7, 'a.md', 1, 2, 'ha', 3, 'markdown', '{"title":"A"}', NULL),
                    (9, 'b.txt', 4, 5, 'hb', 6, 'other', NULL, NULL),
                    (12, 'c.md', 7, 8, 'hc', 9, 'markdown', NULL, 'bad yaml');
                INSERT INTO links (id, source_id, line, kind, type, raw, target) VALUES
                    (21, 7, 1, 'body', 'path', 'b.txt', 'b.txt'),
                    (22, 7, 2, 'body', 'url', 'https://example.com', NULL),
                    (25, 12, 1, 'frontmatter', 'path', '../out.md', NULL);
                INSERT INTO meta (key, value) VALUES ('links', 'fingerprint');
                """);
        }
        List<string> before;
        using (var first = db.Connect())
        {
            before = FirstSchemaRows(first);
        }
        using var connection = db.Connect();

        MigrationRunner.Migrate(connection);

        Assert.Equal(before, FirstSchemaRows(connection));
    }

    /// <summary>Every row of every table, reading only the columns the first schema had, so a later added column does
    /// not count as a change, and leaving out the fingerprint the runner records.</summary>
    private static List<string> FirstSchemaRows(SqliteConnection connection)
    {
        string[] queries =
        [
            "SELECT 'files', id, path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error FROM files ORDER BY id",
            "SELECT 'links', id, source_id, line, kind, type, raw, target FROM links ORDER BY id",
            $"SELECT 'meta', key, value FROM meta WHERE key != '{IndexMeta.Schema}' ORDER BY key",
        ];
        var rows = new List<string>();
        foreach (var query in queries)
        {
            using var reader = connection.ExecuteReader(query);
            while (reader.Read())
            {
                rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
        }
        return rows;
    }

    /// <summary>Script 0004 was once edited in place to add the description column, so an index at the version before
    /// AddSearchDescription has its search table in either shape. Both must come out with every row's rowid, title,
    /// path and body, and an empty description the sweep after the migration fills.</summary>
    [Theory]
    [InlineData("CREATE VIRTUAL TABLE search USING fts5(title, path, body, tokenize = 'porter unicode61')")]
    [InlineData("CREATE VIRTUAL TABLE search USING fts5(title, description, path, body, tokenize = 'porter unicode61')")]
    public void Adding_the_description_keeps_every_search_row_whichever_shape_the_table_had(string create)
    {
        using var db = new TestDatabase();
        var before = MigrationRunner.Scripts.Single(s => s.Name.EndsWith("_AddSearchDescription", StringComparison.Ordinal)).Version - 1;
        using (var first = db.Connect())
        {
            foreach (var script in MigrationRunner.Scripts.Where(s => s.Version <= before))
            {
                first.Execute(script.Sql);
            }
            first.Execute($"PRAGMA user_version = {before}");
            first.Execute("DROP TABLE search");
            first.Execute(create);
            first.Execute("INSERT INTO search (rowid, title, path, body) VALUES (7, 'Heron', 'birds/heron.md', 'a wading bird')");
        }
        using var connection = db.Connect();

        MigrationRunner.Migrate(connection);

        Assert.Equal(
            "7|Heron||birds/heron.md|a wading bird",
            connection.QuerySingle<string>("SELECT rowid || '|' || title || '|' || description || '|' || path || '|' || body FROM search"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM search WHERE search MATCH 'wading'"));
    }

    [Fact]
    public void A_migrated_database_records_the_fingerprint_of_its_scripts()
    {
        using var db = new TestDatabase();
        using var connection = db.Connect();

        MigrationRunner.Migrate(connection);

        Assert.Equal(MigrationRunner.Fingerprint(MigrationRunner.LatestVersion), IndexMeta.Get(connection, IndexMeta.Schema));
    }

    /// <summary>An index built before fingerprints were recorded has none to compare, so it is trusted, and gets one
    /// without a script running, which would rebuild it.</summary>
    [Fact]
    public void A_current_database_with_no_fingerprint_gets_one_and_nothing_else()
    {
        using var db = new TestDatabase();
        using (var first = db.Connect())
        {
            MigrationRunner.Migrate(first);
            first.Execute("DELETE FROM meta WHERE key = @Schema", new { IndexMeta.Schema });
        }
        using var connection = db.Connect();

        Assert.Equal(0, MigrationRunner.Migrate(connection));
        Assert.Equal(MigrationRunner.Fingerprint(MigrationRunner.LatestVersion), IndexMeta.Get(connection, IndexMeta.Schema));
    }

    /// <summary>A script edited after an index ran it leaves the index at a version the binary takes for current, under a
    /// schema the binary does not have. The recorded fingerprint tells them apart.</summary>
    [Fact]
    public void A_database_whose_scripts_differ_from_the_binarys_is_refused()
    {
        using var db = new TestDatabase();
        using (var first = db.Connect())
        {
            MigrationRunner.Migrate(first);
            IndexMeta.Set(first, IndexMeta.Schema, MigrationRunner.Fingerprint(MigrationRunner.LatestVersion - 1));
        }
        using var connection = db.Connect();

        var ex = Assert.Throws<HippoException>(() => MigrationRunner.Migrate(connection));

        Assert.Contains($"was built at schema version {MigrationRunner.LatestVersion} by migration scripts that differ from this hippo's", ex.Message);
        Assert.Contains($"delete {Path.GetDirectoryName(connection.DataSource)} ", ex.Message);
    }

    [Fact]
    public void A_database_newer_than_the_binary_is_refused()
    {
        using var db = new TestDatabase();
        using var connection = db.Connect();
        connection.Execute($"PRAGMA user_version = {MigrationRunner.LatestVersion + 1}");

        var ex = Assert.Throws<HippoException>(() => MigrationRunner.Migrate(connection));

        Assert.Contains("newer", ex.Message);
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'files'"));
    }

    [Fact]
    public async Task Two_connections_migrating_at_once_both_succeed()
    {
        using var db = new TestDatabase();
        using var start = new Barrier(2);

        int Run()
        {
            using var connection = db.Connect();
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return MigrationRunner.Migrate(connection);
        }
        var results = await Task.WhenAll(Task.Run(Run, TestContext.Current.CancellationToken), Task.Run(Run, TestContext.Current.CancellationToken));

        Assert.Equal(MigrationRunner.Scripts.Count, results.Sum());
        using var check = db.Connect();
        Assert.Equal(MigrationRunner.LatestVersion, check.ExecuteScalar<long>("PRAGMA user_version"));
    }
}
