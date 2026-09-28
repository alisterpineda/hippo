using Dapper;
using Hippo.Indexing;

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

    [Fact]
    public void A_version_1_database_gets_an_empty_files_table_of_the_latest_shape()
    {
        using var db = new TestDatabase();
        using (var v1 = db.Connect())
        {
            v1.Execute(MigrationRunner.Scripts[0].Sql);
            v1.Execute("PRAGMA user_version = 1");
            v1.Execute("INSERT INTO files (path, mtime, size, hash, kind) VALUES ('a.md', 0, 0, 'h', 'markdown')");
            // Indexes built before 0001 lost EF's bookkeeping have this table too.
            v1.Execute("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        }
        using var connection = db.Connect();

        var applied = MigrationRunner.Migrate(connection);

        Assert.Equal(MigrationRunner.LatestVersion - 1, applied);
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM files"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM pragma_table_info('files') WHERE name = 'hashed_at'"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'"));
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
