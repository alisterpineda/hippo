using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>Whether an index's workspace is still there, as far as can be told from its recorded root.</summary>
internal enum IndexState
{
    /// <summary>The root has a <c>.hippo/config.json</c>.</summary>
    Live,

    /// <summary>The root has no config, its mount point is mounted, and either the root or the folder above it exists,
    /// so the workspace was deleted, moved or renamed.</summary>
    Orphaned,

    /// <summary>The mount point the root was on is not mounted, as for an unmounted drive; or neither the root nor the
    /// folder above it exists, or they cannot be checked, as for an offline share or a workspace deleted with the folder
    /// above it.</summary>
    Unreachable,

    /// <summary>The index records no root, or cannot be read.</summary>
    Unknown,
}

/// <summary>An index in the cache. <see cref="Root"/> is null when <see cref="State"/> is
/// <see cref="IndexState.Unknown"/>; <see cref="Size"/> counts every file in its folder.</summary>
internal sealed record CachedIndex(string Folder, string? Root, IndexState State, long Size)
{
    public string Database => Path.Combine(Folder, CacheLocation.DatabaseName);
}

/// <summary>The indexes under a cache root: listing them, and removing one whose workspace is gone.</summary>
internal static class CachedIndexes
{
    /// <summary>Ends the name a folder is renamed to while it is removed.</summary>
    private const string RemovingSuffix = ".removing";

    private enum Presence
    {
        Present,
        Absent,
        Unknown,
    }

    /// <summary>Every index under <paramref name="cacheRoot"/>, by root, the ones without a root last, judged against
    /// <paramref name="mountPoints"/>, those mounted now. Folders not named as <see cref="CacheLocation.FolderName"/>
    /// names them are not indexes and are left out.</summary>
    public static List<CachedIndex> List(string cacheRoot, IReadOnlyList<string> mountPoints)
    {
        List<string> folders;
        try
        {
            folders = Directory.Exists(cacheRoot)
                ? Directory.EnumerateDirectories(cacheRoot).Where(f => CacheLocation.IsFolderName(Path.GetFileName(f))).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HippoException($"cannot read {cacheRoot}: {ex.Message}");
        }

        return folders
            .Select(folder =>
            {
                var (root, volume) = ReadRoot(Path.Combine(folder, CacheLocation.DatabaseName));
                var state = root is null ? IndexState.Unknown : StateOf(root, volume, mountPoints);
                return new CachedIndex(folder, root, state, SizeOf(folder));
            })
            .OrderBy(index => index.Root is null)
            .ThenBy(index => index.Root, StringComparer.Ordinal)
            .ThenBy(index => index.Folder, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Removes <paramref name="index"/> by renaming its folder, then deletes the renamed folder. The rename is atomic, so
    /// no process ever opens a half-deleted index, where a leftover <c>-wal</c> file would be replayed into a new
    /// database; on Windows it fails while any process has the index open, which leaves the index whole. A failed rename
    /// throws. A failed delete leaves the index removed all the same, and returns a warning; the next prune retries it.
    /// </summary>
    public static string? Remove(CachedIndex index)
    {
        var removing = $"{index.Folder}.{Guid.NewGuid():n}{RemovingSuffix}";
        Directory.Move(index.Folder, removing);
        try
        {
            Directory.Delete(removing, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cannot delete {removing}: {ex.Message}";
        }
    }

    /// <summary>Deletes the folders a <see cref="Remove"/> renamed but could not delete, returning a warning for each
    /// that still cannot be. Only a folder named as <see cref="Remove"/> names them is touched, since the cache root may
    /// hold folders that are not hippo's.</summary>
    public static List<string> RemoveLeftovers(string cacheRoot)
    {
        var warnings = new List<string>();
        if (!Directory.Exists(cacheRoot))
        {
            return warnings;
        }
        var leftovers = Directory.EnumerateDirectories(cacheRoot, "*" + RemovingSuffix)
            .Where(folder => IsLeftoverName(Path.GetFileName(folder)));
        foreach (var folder in leftovers)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"cannot remove {folder}: {ex.Message}");
            }
        }
        return warnings;
    }

    /// <summary>Whether <paramref name="name"/> is <c>&lt;index folder&gt;.&lt;guid&gt;.removing</c>, as
    /// <see cref="Remove"/> names a folder.</summary>
    private static bool IsLeftoverName(string name) =>
        name.EndsWith(RemovingSuffix, StringComparison.Ordinal)
        && name[..^RemovingSuffix.Length].Split('.') is [var folder, var guid]
        && CacheLocation.IsFolderName(folder)
        && guid.Length == 32 && guid.All(char.IsAsciiHexDigitLower);

    /// <summary>The root <paramref name="database"/> records and the mount point it was on; the root is null when it
    /// records none or cannot be read. Opened read-only and never migrated, so listing changes no index, whichever
    /// hippo wrote it.</summary>
    private static (string? Root, string? Volume) ReadRoot(string database)
    {
        if (!File.Exists(database))
        {
            return (null, null);
        }
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            db.Open();
            return (IndexMeta.Get(db, IndexMeta.Root), IndexMeta.Get(db, IndexMeta.Volume));
        }
        catch (SqliteException)
        {
            return (null, null);
        }
    }

    /// <summary>Checked before the root, since an unmounted drive's mount point, and the folder above it, may still be
    /// there, empty. <paramref name="volume"/> is null or <c>""</c> when no mount point was recorded; then only the root
    /// is checked.</summary>
    private static IndexState StateOf(string root, string? volume, IReadOnlyList<string> mountPoints) =>
        !string.IsNullOrEmpty(volume) && !MountPoints.IsMounted(volume, mountPoints) ? IndexState.Unreachable : StateOf(root);

    private static IndexState StateOf(string root) => Probe(WorkspaceConfig.PathIn(root), directory: false) switch
    {
        Presence.Present => IndexState.Live,
        // A config that cannot be checked may well be there.
        Presence.Unknown => IndexState.Unreachable,
        _ => Probe(root, directory: true) == Presence.Present
            || Path.GetDirectoryName(root) is { } parent && Probe(parent, directory: true) == Presence.Present
                ? IndexState.Orphaned
                : IndexState.Unreachable,
    };

    /// <summary>Whether a file, or with <paramref name="directory"/> a folder, is at <paramref name="path"/>. Only a
    /// path the OS says is not there is <see cref="Presence.Absent"/>; a denied or failed check is
    /// <see cref="Presence.Unknown"/>, so a folder hippo may not look into is never taken for a deleted one.</summary>
    private static Presence Probe(string path, bool directory)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory) == directory ? Presence.Present : Presence.Absent;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Presence.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Presence.Unknown;
        }
    }

    private static long SizeOf(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles().Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
