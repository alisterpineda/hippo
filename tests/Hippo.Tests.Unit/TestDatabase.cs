using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>A SQLite file in a temp folder, built from the embedded migration scripts.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _dir = new();

    public string Path => _dir.Combine("index.db");

    /// <summary>Opens the index through hippo, which migrates it.</summary>
    internal SqliteConnection Open() => IndexDatabase.Open(Path);

    /// <summary>A plain connection, with no migration applied.</summary>
    public SqliteConnection Connect() => Connect(Path);

    public static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => _dir.Dispose();
}
