using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>What one sweep found. <see cref="Hashed"/> counts files read from disk; <see cref="Updated"/> counts files
/// whose content changed.</summary>
internal sealed record SweepResult(
    int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt,
    DateTimeOffset FinishedAt, TimeSpan Elapsed, IReadOnlyList<string> Warnings);

/// <summary>
/// Brings the <c>files</c> and <c>links</c> tables in line with the workspace on disk: enumerate, stat, re-hash only when
/// mtime or size changed or the row is racy, re-parse only when the hash changed, and drop rows for files that are gone.
/// A change to the settings that shape links re-parses every page. Every write is idempotent, so several processes may
/// sweep the same workspace at once.
/// </summary>
internal static class Sweeper
{
    private const int BatchSize = 200;

    /// <summary>How close to its hash time a file's mtime may be before its row is not trusted: FAT's 2 s mtime
    /// granularity, the coarsest hippo allows for.</summary>
    private static readonly TimeSpan RacyMargin = TimeSpan.FromSeconds(2);

    /// <summary>A listed file, its mtime in the unit the index stores.</summary>
    private sealed record DiskFile(string Path, string FullPath, long Size, long Mtime);

    // Internal, not private: the code Dapper.AOT generates must reach these types.
    internal sealed record KnownFile(string Path, long Mtime, long Size, string Hash, long HashedAt);

    /// <summary>The size every string of a row written in batches carries. Dapper.AOT sizes a string parameter from the
    /// first row of a batch, to 4,000 unless that value is longer, and keeps the size for the rows after it, which
    /// Microsoft.Data.Sqlite then truncates to.</summary>
    internal const int Unsized = -1;

    internal sealed record FileRow(
        [property: DbValue(Size = Unsized)] string Path,
        long Mtime,
        long Size,
        [property: DbValue(Size = Unsized)] string Hash,
        long HashedAt,
        [property: DbValue(Size = Unsized)] string Kind,
        [property: DbValue(Size = Unsized)] string? Frontmatter,
        [property: DbValue(Size = Unsized)] string? ParseError);

    internal sealed record StatRow(
        [property: DbValue(Size = Unsized)] string Path,
        long Mtime,
        long Size,
        [property: DbValue(Size = Unsized)] string Hash,
        long HashedAt);

    internal sealed record PathRow([property: DbValue(Size = Unsized)] string Path);

    /// <summary>A page's links are written only beside the file row they were parsed with, matched by hash, so a page's
    /// links always belong to the content its row describes.</summary>
    internal sealed record SourceRow(
        [property: DbValue(Size = Unsized)] string Path,
        [property: DbValue(Size = Unsized)] string Hash);

    internal sealed record LinkRow(
        [property: DbValue(Size = Unsized)] string Path,
        [property: DbValue(Size = Unsized)] string Hash,
        int Line,
        [property: DbValue(Size = Unsized)] string Kind,
        [property: DbValue(Size = Unsized)] string Type,
        [property: DbValue(Size = Unsized)] string Raw,
        [property: DbValue(Size = Unsized)] string? Target);

    private sealed record ParsedFile(FileRow Row, List<LinkRow> Links);

    public static SweepResult Run(Workspace workspace, SqliteConnection db, bool rebuild, TimeProvider clock)
    {
        var stopwatch = Stopwatch.StartNew();
        // Read once, before any file is listed, so it is no later than any read below; an earlier time can only make a
        // row look racy more often, never less.
        var hashedAt = Ticks(clock.GetUtcNow());
        var warnings = new List<string>();
        // Links extracted under other settings would resolve differently now, so every page is parsed again. Only
        // pages have links, so every other file is still skipped when its stats show it unchanged.
        var settings = workspace.Config.Links;
        var linkSettings = settings.Fingerprint;
        var relink = IndexMeta.Get(db, IndexMeta.LinkSettings) != linkSettings;
        // A page that cannot be read keeps its old links, so the new settings are recorded only once every page was
        // read under them; until then each sweep parses the pages again.
        var unreadPage = false;
        var known = db.Query<KnownFile>("SELECT path, mtime, size, hash, hashed_at AS HashedAt FROM files").ToDictionary(k => k.Path, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ParsedFile>();
        var stats = new List<StatRow>();
        int added = 0, updated = 0, hashed = 0;
        foreach (var file in workspace.ListFiles(warnings).Select(f => new DiskFile(f.Path, f.FullPath, f.Size, Ticks(f.Modified))))
        {
            known.TryGetValue(file.Path, out var previous);
            var reparse = rebuild || relink && Workspace.IsMarkdown(file.Path);
            if (!reparse && previous is not null && Unchanged(previous, file))
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
                if (Workspace.IsMarkdown(file.Path))
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
                unreadPage |= Workspace.IsMarkdown(file.Path);
                continue;
            }
            seen.Add(file.Path);
            hashed++;

            if (!reparse && previous is not null && previous.Hash == hash)
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
            var parsed = Parse(settings, file, hash, hashedAt, content, out var bodyError);
            if (bodyError is not null)
            {
                warnings.Add($"cannot read the links in {file.Path}: {bodyError}");
            }
            rows.Add(parsed);
        }

        var removed = known.Keys.Where(path => !seen.Contains(path)).Select(path => new PathRow(path)).ToList();
        if (rebuild || relink)
        {
            // One transaction, so a reader never sees a half-built index.
            // Rows of files that could not be read are kept, as an ordinary sweep keeps them. Each re-read row (every
            // file on a rebuild, the pages and changed files on a relink) is rewritten in place, so it keeps its id and
            // whatever references it.
            using var transaction = db.BeginTransaction(deferred: false);
            db.Execute("DELETE FROM files WHERE path = @Path", removed, transaction);
            Write(db, rows, overwrite: true, transaction);
            db.Execute("UPDATE files SET mtime = @Mtime, size = @Size, hashed_at = @HashedAt WHERE path = @Path AND hash = @Hash", stats, transaction);
            if (!unreadPage)
            {
                IndexMeta.Set(db, IndexMeta.LinkSettings, linkSettings, transaction);
            }
            transaction.Commit();
        }
        else
        {
            // Each call names its row type, which Dapper.AOT needs to generate the binding.
            InBatches(db, rows, (batch, transaction) => Write(db, batch, overwrite: false, transaction));
            InBatches(db, stats, (batch, transaction) =>
                db.Execute("UPDATE files SET mtime = @Mtime, size = @Size, hashed_at = @HashedAt WHERE path = @Path AND hash = @Hash", batch, transaction));
            InBatches(db, removed, (batch, transaction) => db.Execute("DELETE FROM files WHERE path = @Path", batch, transaction));
        }

        return new SweepResult(seen.Count, added, updated, removed.Count, hashed, rebuild || relink, clock.GetUtcNow(), stopwatch.Elapsed, warnings);
    }

    /// <summary>Writes each row whatever it held, keeping the id of a row that was there: a rebuild rewrites what a new
    /// hippo may derive differently from unchanged content, and a relink records the stats of a page it re-read.</summary>
    private const string OverwriteSql = """
        INSERT INTO files (path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error)
        VALUES (@Path, @Mtime, @Size, @Hash, @HashedAt, @Kind, @Frontmatter, @ParseError)
        ON CONFLICT (path) DO UPDATE SET
            mtime = excluded.mtime, size = excluded.size, hash = excluded.hash, hashed_at = excluded.hashed_at,
            kind = excluded.kind, frontmatter = excluded.frontmatter, parse_error = excluded.parse_error
        """;

    /// <summary>A row changes only when its content does, so a second process writing the same file is a no-op.</summary>
    private const string UpsertSql = OverwriteSql + """

        WHERE excluded.hash != files.hash
        """;

    /// <summary>Writes parsed files and replaces each page's links, in the caller's transaction so a reader never sees a
    /// page without its links. <paramref name="overwrite"/> rewrites rows whose content is unchanged.</summary>
    private static void Write(SqliteConnection db, List<ParsedFile> files, bool overwrite, SqliteTransaction transaction)
    {
        var rows = files.Select(f => f.Row).ToList();
        if (overwrite)
        {
            db.Execute(OverwriteSql, rows, transaction);
        }
        else
        {
            db.Execute(UpsertSql, rows, transaction);
        }
        db.Execute(
            "DELETE FROM links WHERE source_id = (SELECT id FROM files WHERE path = @Path AND hash = @Hash)",
            files.Where(f => f.Row.Kind == "markdown").Select(f => new SourceRow(f.Row.Path, f.Row.Hash)).ToList(), transaction);
        db.Execute("""
            INSERT INTO links (source_id, line, kind, type, raw, target)
            SELECT id, @Line, @Kind, @Type, @Raw, @Target FROM files WHERE path = @Path AND hash = @Hash
            """, files.SelectMany(f => f.Links).ToList(), transaction);
    }

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

    private static ParsedFile Parse(LinkSettings settings, DiskFile file, string hash, long hashedAt, byte[]? content, out string? bodyError)
    {
        bodyError = null;
        if (!Workspace.IsMarkdown(file.Path))
        {
            return new ParsedFile(new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "other", null, null), []);
        }
        var page = Page.Parse(file.Path, content!, settings);
        bodyError = page.BodyError;
        return new ParsedFile(
            new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "markdown", page.Frontmatter.Json, page.Frontmatter.Error),
            page.Links.Select(l => new LinkRow(file.Path, hash, l.Line, l.Kind, l.Type, l.Raw, l.Target)).ToList());
    }
}
