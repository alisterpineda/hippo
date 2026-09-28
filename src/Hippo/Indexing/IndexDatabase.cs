using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

internal static class IndexDatabase
{
    /// <summary>Opens (creating if needed) the index at <paramref name="path"/> and migrates it to this binary's
    /// schema. <paramref name="scriptsApplied"/> is nonzero when the schema changed, which calls for a full reindex.</summary>
    public static SqliteConnection Open(string path, out int scriptsApplied)
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
            scriptsApplied = MigrationRunner.Migrate(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
