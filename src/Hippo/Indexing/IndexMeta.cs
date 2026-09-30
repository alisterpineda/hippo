using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>The <c>meta</c> table: facts about how an index was built and whose it is, one value per key.</summary>
internal static class IndexMeta
{
    /// <summary>The fingerprint of the link settings the index's links were extracted under.</summary>
    public const string LinkSettings = "links";

    /// <summary>The canonical root of the workspace the index belongs to, which tells <c>hippo cache</c> whether that
    /// workspace is still there.</summary>
    public const string Root = "root";

    /// <summary>The mount point <see cref="Root"/> was on when recorded, or <c>""</c> when it was on none that could be
    /// found. While that mount point is not mounted, <c>hippo cache</c> takes the workspace for unreachable, not gone.</summary>
    public const string Volume = "volume";

    // Internal, not private: the code Dapper.AOT generates must reach it.
    internal sealed record MetaRow(string Key, string Value);

    public static string? Get(SqliteConnection db, string key) =>
        db.QuerySingleOrDefault<string>("SELECT value FROM meta WHERE key = @key", new { key });

    public static void Set(SqliteConnection db, string key, string value, SqliteTransaction? transaction = null) =>
        db.Execute("""
            INSERT INTO meta (key, value) VALUES (@Key, @Value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """, new MetaRow(key, value), transaction);

    /// <summary>Records <paramref name="root"/> as the index's workspace, with the mount point it is on, writing only
    /// when they are not recorded already. The mount points are listed only then, so most commands never list them.</summary>
    public static void RecordRoot(SqliteConnection db, string root, Func<IReadOnlyList<string>> getMountPoints)
    {
        if (Get(db, Root) == root && Get(db, Volume) is not null)
        {
            return;
        }
        var volume = MountPoints.Containing(root, getMountPoints()) ?? "";
        using var transaction = db.BeginTransaction(deferred: false);
        Set(db, Root, root, transaction);
        Set(db, Volume, volume, transaction);
        transaction.Commit();
    }
}
