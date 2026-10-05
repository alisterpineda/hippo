using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

internal static class IndexDatabase
{
    /// <summary>Opens (creating if needed) the index at <paramref name="path"/> and migrates it to this binary's
    /// schema. A schema change calls for a full reindex, so the migration runner leaves
    /// <see cref="IndexMeta.RebuildPending"/> set until one commits.</summary>
    public static SqliteConnection Open(string path)
    {
        // The index holds every path and all frontmatter, so only the user may read it.
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        try
        {
            connection.Open();
            MigrationRunner.Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
