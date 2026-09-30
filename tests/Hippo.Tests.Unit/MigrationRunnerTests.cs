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
    /// not count as a change.</summary>
    private static List<string> FirstSchemaRows(SqliteConnection connection)
    {
        string[] queries =
        [
            "SELECT 'files', id, path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error FROM files ORDER BY id",
            "SELECT 'links', id, source_id, line, kind, type, raw, target FROM links ORDER BY id",
            "SELECT 'meta', key, value FROM meta ORDER BY key",
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
