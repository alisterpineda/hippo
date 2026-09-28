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
/// changed, re-parse only when the hash changed, and drop rows for files that are gone. Every write is idempotent, so
/// several processes may sweep the same notebook at once.
/// </summary>
internal static class Sweeper
{
    private const int BatchSize = 200;

    private sealed record DiskFile(string Path, string FullPath, long Size, long Mtime);

    // Internal, not private: the code Dapper.AOT generates must reach these types.
    internal sealed record KnownFile(string Path, long Mtime, long Size, string Hash);

    internal sealed record FileRow(string Path, long Mtime, long Size, string Hash, string Kind, string? Frontmatter, string? ParseError);

    internal sealed record StatRow(string Path, long Mtime, long Size, string Hash);

    internal sealed record PathRow(string Path);

    public static SweepResult Run(Notebook notebook, SqliteConnection db, bool rebuild)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var known = db.Query<KnownFile>("SELECT path, mtime, size, hash FROM files").ToDictionary(k => k.Path, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<FileRow>();
        var stats = new List<StatRow>();
        int added = 0, updated = 0, hashed = 0;
        foreach (var file in Enumerate(notebook))
        {
            known.TryGetValue(file.Path, out var previous);
            if (!rebuild && previous is not null && previous.Mtime == file.Mtime && previous.Size == file.Size)
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
                stats.Add(new StatRow(file.Path, file.Mtime, file.Size, hash));
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
            rows.Add(Parse(file, hash, content));
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
                db.Execute("UPDATE files SET mtime = @Mtime, size = @Size WHERE path = @Path AND hash = @Hash", batch, transaction));
            InBatches(db, removed, (batch, transaction) => db.Execute("DELETE FROM files WHERE path = @Path", batch, transaction));
        }

        return new SweepResult(seen.Count, added, updated, removed.Count, hashed, rebuild, DateTimeOffset.UtcNow, stopwatch.Elapsed, warnings);
    }

    /// <summary>A row changes only when its content does, so a second process writing the same file is a no-op.</summary>
    private const string UpsertSql = """
        INSERT INTO files (path, mtime, size, hash, kind, frontmatter, parse_error)
        VALUES (@Path, @Mtime, @Size, @Hash, @Kind, @Frontmatter, @ParseError)
        ON CONFLICT (path) DO UPDATE SET
            mtime = excluded.mtime, size = excluded.size, hash = excluded.hash, kind = excluded.kind,
            frontmatter = excluded.frontmatter, parse_error = excluded.parse_error
        WHERE excluded.hash != files.hash
        """;

    private static void InBatches<T>(SqliteConnection db, List<T> items, Action<List<T>, SqliteTransaction> write)
    {
        for (var start = 0; start < items.Count; start += BatchSize)
        {
            using var transaction = db.BeginTransaction(deferred: false);
            write(items.GetRange(start, Math.Min(BatchSize, items.Count - start)), transaction);
            transaction.Commit();
        }
    }

    private static FileRow Parse(DiskFile file, string hash, byte[]? content)
    {
        if (!Notebook.IsMarkdown(file.Path))
        {
            return new FileRow(file.Path, file.Mtime, file.Size, hash, "plain", null, null);
        }
        var frontmatter = Frontmatter.Parse(content!);
        return new FileRow(file.Path, file.Mtime, file.Size, hash, "markdown", frontmatter.Json, frontmatter.Error);
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
                (entry.LastWriteTimeUtc - DateTimeOffset.UnixEpoch).Ticks),
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
