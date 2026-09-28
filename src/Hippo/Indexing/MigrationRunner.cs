using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>
/// Brings a database up to the binary's schema by running the embedded <c>Migrations/NNNN_&lt;name&gt;.sql</c> scripts
/// whose number is above its schema version. Safe for several processes at once: the version is read under the write
/// lock, so a second process waits and then finds nothing to do.
/// </summary>
internal static class MigrationRunner
{
    private const string ResourcePrefix = "Hippo.Migrations.";

    internal sealed record Script(int Version, string Sql);

    public static IReadOnlyList<Script> Scripts { get; } = LoadScripts();

    public static int LatestVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    /// <summary>Applies every newer script and returns how many ran.</summary>
    public static int Migrate(SqliteConnection connection)
    {
        Execute(connection, null, "PRAGMA busy_timeout = 30000");
        Execute(connection, null, "PRAGMA journal_mode = WAL");
        // SQLite ignores this pragma inside a transaction, and a table rebuild needs it off.
        Execute(connection, null, "PRAGMA foreign_keys = OFF");

        var applied = 0;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            // The schema version lives in SQLite's user_version header field, which SQLite reserves for the application
            // and never touches.
            var version = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (version > LatestVersion)
            {
                throw new HippoException(
                    $"the index at {connection.DataSource} has schema version {version}, newer than this hippo supports ({LatestVersion}); upgrade hippo");
            }

            foreach (var script in Scripts.Where(s => s.Version > version))
            {
                Execute(connection, transaction, script.Sql);
                Execute(connection, transaction, $"PRAGMA user_version = {script.Version}");
                applied++;
            }

            if (Scalar(connection, transaction, "PRAGMA foreign_key_check") is not null)
            {
                throw new InvalidOperationException("the migrated index fails its foreign key check");
            }
            transaction.Commit();
        }

        Execute(connection, null, "PRAGMA foreign_keys = ON");
        return applied;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<Script> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var scripts = new List<Script>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }
            var stem = resource[ResourcePrefix.Length..^".sql".Length];
            var separator = stem.IndexOf('_');
            if (separator <= 0 || separator == stem.Length - 1
                || !int.TryParse(stem[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException(
                    $"embedded migration script {resource} is not named NNNN_<name>.sql");
            }
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            scripts.Add(new Script(version, reader.ReadToEnd()));
        }
        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        return scripts;
    }
}
