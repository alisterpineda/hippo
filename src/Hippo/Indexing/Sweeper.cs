using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using Dapper;
using Hippo.Notebooks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Indexing;

/// <summary>What one sweep found. <see cref="Hashed"/> counts files read from disk; <see cref="Updated"/> counts files
/// whose content changed.</summary>
internal sealed record SweepResult(
    int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt,
    DateTimeOffset FinishedAt, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

/// <summary>
/// Brings the <c>files</c> table in line with the notebook on disk: enumerate, stat, re-hash only when mtime or size
/// changed or the row is racy, re-parse only when the hash changed, and drop rows for files that are gone. Every write
/// is idempotent, so several processes may sweep the same notebook at once.
/// </summary>
internal static class Sweeper
{
    private const int BatchSize = 200;

    /// <summary>How close to its hash time a file's mtime may be before its row is not trusted: FAT's 2 s mtime
    /// granularity, the coarsest hippo allows for.</summary>
    private static readonly TimeSpan RacyMargin = TimeSpan.FromSeconds(2);

    private sealed record DiskFile(string Path, string FullPath, long Size, long Mtime);

    // Internal, not private: the code Dapper.AOT generates must reach these types.
    internal sealed record KnownFile(string Path, long Mtime, long Size, string Hash, long HashedAt);

    internal sealed record FileRow(string Path, long Mtime, long Size, string Hash, long HashedAt, string Kind, string? Frontmatter, string? ParseError);

    internal sealed record StatRow(string Path, long Mtime, long Size, string Hash, long HashedAt);

    internal sealed record PathRow(string Path);

    public static SweepResult Run(Notebook notebook, SqliteConnection db, bool rebuild, TimeProvider clock)
    {
        var stopwatch = Stopwatch.StartNew();
        // Read once, before any file is listed, so it is no later than any read below; an earlier time can only make a
        // row look racy more often, never less.
        var hashedAt = Ticks(clock.GetUtcNow());
        var warnings = new List<string>();
        var known = db.Query<KnownFile>("SELECT path, mtime, size, hash, hashed_at AS HashedAt FROM files").ToDictionary(k => k.Path, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<FileRow>();
        var stats = new List<StatRow>();
        int added = 0, updated = 0, hashed = 0;
        foreach (var file in Enumerate(notebook))
        {
            known.TryGetValue(file.Path, out var previous);
            if (!rebuild && previous is not null && Unchanged(previous, file))
            {
                seen.Add(file.Path);
                continue;
            }

            // Only markdown needs its bytes, for frontmatter; any other file is hashed as a stream so an attachment of
            // any size costs constant memory.
            byte[]? content = null;
            string hash;
            try
            {
                if (Notebook.IsMarkdown(file.Path))
                {
                    content = File.ReadAllBytes(file.FullPath);
                    hash = Convert.ToHexStringLower(SHA256.HashData(content));
                }
                else
                {
                    using var stream = File.OpenRead(file.FullPath);
                    hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // Deleted since it was listed; its row goes with the others that are gone.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its row, if any, stays as it was until the file can be read again.
                warnings.Add($"cannot read {file.Path}: {ex.Message}");
                seen.Add(file.Path);
                continue;
            }
            seen.Add(file.Path);
            hashed++;

            if (!rebuild && previous is not null && previous.Hash == hash)
            {
                // A row that would still be racy with the same stats gains nothing from a later hash time, so a file
                // dated in the future costs no write on every sweep.
                if (previous.Mtime != file.Mtime || previous.Size != file.Size || !Racy(file.Mtime, hashedAt))
                {
                    stats.Add(new StatRow(file.Path, file.Mtime, file.Size, hash, hashedAt));
                }
                continue;
            }

            if (previous is null)
            {
                added++;
            }
            else if (previous.Hash != hash)
            {
                updated++;
            }
            rows.Add(Parse(file, hash, hashedAt, content));
        }

        var removed = known.Keys.Where(path => !seen.Contains(path)).Select(path => new PathRow(path)).ToList();
        if (rebuild)
        {
            // One transaction, so a reader never sees a half-built index.
            // Rows of files that could not be read are kept, as an ordinary sweep keeps them.
            using var transaction = db.BeginTransaction(deferred: false);
            db.Execute("DELETE FROM files WHERE path = @Path", rows.Select(r => new PathRow(r.Path)).Concat(removed).ToList(), transaction);
            db.Execute(UpsertSql, rows, transaction);
            transaction.Commit();
        }
        else
        {
            // Each call names its row type, which Dapper.AOT needs to generate the binding.
            InBatches(db, rows, (batch, transaction) => db.Execute(UpsertSql, batch, transaction));
            InBatches(db, stats, (batch, transaction) =>
                db.Execute("UPDATE files SET mtime = @Mtime, size = @Size, hashed_at = @HashedAt WHERE path = @Path AND hash = @Hash", batch, transaction));
            InBatches(db, removed, (batch, transaction) => db.Execute("DELETE FROM files WHERE path = @Path", batch, transaction));
        }

        return new SweepResult(seen.Count, added, updated, removed.Count, hashed, rebuild, clock.GetUtcNow(), stopwatch.Elapsed, warnings);
    }

    /// <summary>A row changes only when its content does, so a second process writing the same file is a no-op.</summary>
    private const string UpsertSql = """
        INSERT INTO files (path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error)
        VALUES (@Path, @Mtime, @Size, @Hash, @HashedAt, @Kind, @Frontmatter, @ParseError)
        ON CONFLICT (path) DO UPDATE SET
            mtime = excluded.mtime, size = excluded.size, hash = excluded.hash, hashed_at = excluded.hashed_at,
            kind = excluded.kind, frontmatter = excluded.frontmatter, parse_error = excluded.parse_error
        WHERE excluded.hash != files.hash
        """;

    /// <summary>Whether the row still describes the file: same mtime and size, and not racy.</summary>
    private static bool Unchanged(KnownFile previous, DiskFile file) =>
        previous.Mtime == file.Mtime && previous.Size == file.Size && !Racy(previous.Mtime, previous.HashedAt);

    /// <summary>Whether a file with this mtime, hashed at <paramref name="hashedAt"/>, may since have changed again in
    /// the same mtime tick without changing its size. Such a row is not trusted until a sweep at least
    /// <see cref="RacyMargin"/> after the mtime re-hashes the file.</summary>
    private static bool Racy(long mtime, long hashedAt) => mtime >= hashedAt - RacyMargin.Ticks;

    /// <summary>A time in the unit the index stores: 100 ns ticks since the Unix epoch, UTC.</summary>
    private static long Ticks(DateTimeOffset time) => (time - DateTimeOffset.UnixEpoch).Ticks;

    private static void InBatches<T>(SqliteConnection db, List<T> items, Action<List<T>, SqliteTransaction> write)
    {
        for (var start = 0; start < items.Count; start += BatchSize)
        {
            using var transaction = db.BeginTransaction(deferred: false);
            write(items.GetRange(start, Math.Min(BatchSize, items.Count - start)), transaction);
            transaction.Commit();
        }
    }

    private static FileRow Parse(DiskFile file, string hash, long hashedAt, byte[]? content)
    {
        if (!Notebook.IsMarkdown(file.Path))
        {
            return new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "plain", null, null);
        }
        var frontmatter = Frontmatter.Parse(content!);
        return new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "markdown", frontmatter.Json, frontmatter.Error);
    }

    /// <summary>Lists the included files under the root. Symbolic links are skipped, so nothing outside the root is
    /// read, and a folder that an exclude pattern ending in <c>/**</c> covers is never entered.</summary>
    private static List<DiskFile> Enumerate(Notebook notebook)
    {
        var root = notebook.Root;
        var pruned = notebook.Config.Exclude
            .Where(pattern => pattern.EndsWith("/**", StringComparison.Ordinal))
            .Select(pattern =>
            {
                var folder = new Matcher(StringComparison.Ordinal);
                folder.AddInclude(pattern[..^3]);
                return folder;
            })
            .ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
        };
        var files = new FileSystemEnumerable<DiskFile>(
            root,
            (ref entry) => new DiskFile(
                RelativePath(ref entry),
                entry.ToFullPath(),
                entry.Length,
                Ticks(entry.LastWriteTimeUtc)),
            options)
        {
            ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && !IsLink(ref entry),
            ShouldRecursePredicate = (ref entry) =>
            {
                if (IsLink(ref entry))
                {
                    return false;
                }
                var path = RelativePath(ref entry);
                return !pruned.Exists(folder => folder.Match(root, path).HasMatches);
            },
        }.ToList();

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(notebook.Config.Include);
        matcher.AddExcludePatterns(notebook.Config.Exclude);
        var included = notebook.Match(matcher, files.Select(f => f.FullPath));
        return files.Where(f => included.Contains(f.Path)).ToList();
    }

    private static bool IsLink(ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string RelativePath(ref FileSystemEntry entry)
    {
        var directory = entry.Directory[entry.RootDirectory.Length..].TrimStart(['/', '\\']);
        var path = directory.IsEmpty ? entry.FileName.ToString() : $"{directory}/{entry.FileName}";
        return Notebook.Key(path);
    }
}
