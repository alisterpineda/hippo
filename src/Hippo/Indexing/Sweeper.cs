using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Hippo.Okf;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>What one sweep found. <see cref="Hashed"/> counts files read from disk; <see cref="Updated"/> counts files
/// whose content changed. <see cref="OkfBundles"/> are the OKF bundles the pages were read under.</summary>
internal sealed record SweepResult(
    int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt,
    DateTimeOffset FinishedAt, TimeSpan Elapsed, IReadOnlyList<string> Warnings, IReadOnlyList<OkfBundle> OkfBundles);

/// <summary>
/// Brings the <c>files</c>, <c>links</c>, <c>findings</c>, <c>index_entries</c> and <c>search</c> tables in line with the workspace on disk: enumerate, stat,
/// re-hash only when mtime or size changed or the row is racy, re-parse only when the hash changed, and drop rows for
/// files that are gone. A change to the settings that shape links and findings, such as a bundle becoming an OKF bundle,
/// re-parses every page. Every write is idempotent, so several processes may sweep the same workspace at once.
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

    internal sealed record FindingRow(
        [property: DbValue(Size = Unsized)] string Path,
        [property: DbValue(Size = Unsized)] string Hash,
        [property: DbValue(Size = Unsized)] string Rule,
        int? Line,
        [property: DbValue(Size = Unsized)] string Message);

    internal sealed record EntryRow(
        [property: DbValue(Size = Unsized)] string Path,
        [property: DbValue(Size = Unsized)] string Hash,
        int Line,
        [property: DbValue(Size = Unsized)] string Target,
        [property: DbValue(Size = Unsized)] string? Description);

    internal sealed record SearchRow(
        [property: DbValue(Size = Unsized)] string Path,
        [property: DbValue(Size = Unsized)] string Hash,
        [property: DbValue(Size = Unsized)] string Title,
        [property: DbValue(Size = Unsized)] string Body);

    /// <summary>A parsed file; <see cref="Search"/> is null for a file that is not markdown, and for a page whose search
    /// row is kept as it is.</summary>
    private sealed record ParsedFile(FileRow Row, List<LinkRow> Links, List<FindingRow> Findings, List<EntryRow> Entries, SearchRow? Search);

    public static SweepResult Run(Workspace workspace, SqliteConnection db, bool rebuild, TimeProvider clock)
    {
        var stopwatch = Stopwatch.StartNew();
        // Read once, before any file is listed, so it is no later than any read below; an earlier time can only make a
        // row look racy more often, never less.
        var hashedAt = Ticks(clock.GetUtcNow());
        var warnings = new List<string>();
        SearchIndex.UseTokenizer(db, workspace.Config.SearchTokenizer);
        var listed = workspace.ListFiles(warnings);
        // The parse warnings report what a frontmatter-syntax finding does, so lint.exclude silences both. The globs are
        // matched only once a file fails to parse, so a sweep without parse errors does no glob work.
        HashSet<string>? quiet = null;
        bool Quiet(string path) => (quiet ??= workspace.LintExcluded(listed.Select(f => f.Path))).Contains(path);
        var okfBundles = OkfBundle.Find(workspace, listed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal));
        // Links extracted and findings made under other settings would differ now, so every page is parsed again. Only
        // pages have links and findings, so every other file is still skipped when its stats show it unchanged.
        var settings = new PageSettings(workspace.Config.Bundles, workspace.Config.Links, okfBundles.Select(b => b.Root).ToList());
        var linkSettings = settings.Fingerprint;
        var relink = IndexMeta.Get(db, IndexMeta.LinkSettings) != linkSettings;
        // A page that cannot be read keeps its old links, so the new settings are recorded only once every page was
        // read under them; until then each sweep parses the pages again.
        var unreadPage = false;

        // Parsed files are written a batch at a time as they come, so a sweep holds no more than one batch of page
        // bodies however large the workspace. A rebuild or relink writes them all in one transaction, so a reader never
        // sees a half-built index; it takes the write lock before reading the first file, and other writers wait for it
        // while the files are read. Readers do not: under WAL they go on seeing the index as it was.
        var bulk = rebuild || relink;
        using var transaction = bulk ? db.BeginTransaction(deferred: false) : null;
        var rows = transaction is null
            ? new Batches<ParsedFile>(BatchSize, batch => InTransaction(db, t => Write(db, batch, overwrite: false, t)))
            // Each re-read row (every file on a rebuild, the pages and changed files on a relink) is rewritten in
            // place, so it keeps its id and whatever references it.
            : new Batches<ParsedFile>(BatchSize, batch => Write(db, batch, overwrite: true, transaction));
        var known = db.Query<KnownFile>("SELECT path, mtime, size, hash, hashed_at AS HashedAt FROM files", transaction: transaction)
            .ToDictionary(k => k.Path, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stats = new List<StatRow>();
        int added = 0, updated = 0, hashed = 0;
        foreach (var file in listed.Select(f => new DiskFile(f.Path, f.FullPath, f.Size, Ticks(f.Modified))))
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
            if (!rebuild && previous is not null && previous.Hash == hash)
            {
                // A page re-read for a relink has the title, path and body it had, so its search row stays as it is.
                parsed = parsed with { Search = null };
            }
            if (parsed.Row.ParseError is not null && !Quiet(file.Path))
            {
                warnings.Add($"cannot read the frontmatter in {file.Path}: {parsed.Row.ParseError}");
            }
            if (bodyError is not null && !Quiet(file.Path))
            {
                warnings.Add($"cannot read the links in {file.Path}: {bodyError}");
            }
            rows.Add(parsed);
        }

        rows.Flush();

        var removed = known.Keys.Where(path => !seen.Contains(path)).Select(path => new PathRow(path)).ToList();
        if (transaction is not null)
        {
            // Rows of files that could not be read are kept, as an ordinary sweep keeps them.
            Remove(db, removed, transaction);
            db.Execute("UPDATE files SET mtime = @Mtime, size = @Size, hashed_at = @HashedAt WHERE path = @Path AND hash = @Hash", stats, transaction);
            // With a page unread, a value no settings have is recorded, not the old one: the settings can change back
            // to the old ones, as when a bundle's root index.md cannot be read for one sweep, and the pages this sweep
            // parsed under the new ones must still be parsed again.
            IndexMeta.Set(db, IndexMeta.LinkSettings, unreadPage ? "" : linkSettings, transaction);
            transaction.Commit();
        }
        else
        {
            // Each call names its row type, which Dapper.AOT needs to generate the binding.
            InBatches(db, stats, (batch, t) =>
                db.Execute("UPDATE files SET mtime = @Mtime, size = @Size, hashed_at = @HashedAt WHERE path = @Path AND hash = @Hash", batch, t));
            InBatches(db, removed, (batch, t) => Remove(db, batch, t));
        }

        return new SweepResult(seen.Count, added, updated, removed.Count, hashed, bulk, clock.GetUtcNow(), stopwatch.Elapsed, warnings, okfBundles);
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

    /// <summary>Writes parsed files and replaces each page's links, findings, index entries and search row (when it has
    /// a <see cref="ParsedFile.Search"/>), in the caller's transaction so a reader never sees a page without them. <paramref name="overwrite"/> rewrites rows whose content is unchanged.</summary>
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
        var pages = files.Where(f => f.Row.Kind == "markdown").Select(f => new SourceRow(f.Row.Path, f.Row.Hash)).ToList();
        db.Execute("DELETE FROM links WHERE source_id = (SELECT id FROM files WHERE path = @Path AND hash = @Hash)", pages, transaction);
        db.Execute("DELETE FROM findings WHERE file_id = (SELECT id FROM files WHERE path = @Path AND hash = @Hash)", pages, transaction);
        db.Execute("DELETE FROM index_entries WHERE file_id = (SELECT id FROM files WHERE path = @Path AND hash = @Hash)", pages, transaction);
        var searched = files.Select(f => f.Search).OfType<SearchRow>().Select(s => new SourceRow(s.Path, s.Hash)).ToList();
        db.Execute("DELETE FROM search WHERE rowid = (SELECT id FROM files WHERE path = @Path AND hash = @Hash)", searched, transaction);
        db.Execute("""
            INSERT INTO links (source_id, line, kind, type, raw, target)
            SELECT id, @Line, @Kind, @Type, @Raw, @Target FROM files WHERE path = @Path AND hash = @Hash
            """, files.SelectMany(f => f.Links).ToList(), transaction);
        // No rule yet reports other paths, so every finding's related list is empty.
        db.Execute("""
            INSERT INTO findings (file_id, rule, line, message, related)
            SELECT id, @Rule, @Line, @Message, '[]' FROM files WHERE path = @Path AND hash = @Hash
            """, files.SelectMany(f => f.Findings).ToList(), transaction);
        db.Execute("""
            INSERT INTO index_entries (file_id, line, target, description)
            SELECT id, @Line, @Target, @Description FROM files WHERE path = @Path AND hash = @Hash
            """, files.SelectMany(f => f.Entries).ToList(), transaction);
        // The values follow SearchIndex.Columns.
        db.Execute($"""
            INSERT INTO search (rowid, {SearchIndex.Columns})
            SELECT id, @Title, @Path, @Body FROM files WHERE path = @Path AND hash = @Hash
            """, files.Select(f => f.Search).OfType<SearchRow>().ToList(), transaction);
    }

    /// <summary>Drops the rows of files that are gone. Their links, findings and index entries go with them by cascade;
    /// their search rows, which no foreign key ties to them, by rowid first, while the files' ids can still be read.</summary>
    private static void Remove(SqliteConnection db, List<PathRow> removed, SqliteTransaction transaction)
    {
        if (removed.Count == 0)
        {
            return;
        }
        db.Execute("DELETE FROM search WHERE rowid = (SELECT id FROM files WHERE path = @Path)", removed, transaction);
        db.Execute("DELETE FROM files WHERE path = @Path", removed, transaction);
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
            InTransaction(db, transaction => write(items.GetRange(start, Math.Min(BatchSize, items.Count - start)), transaction));
        }
    }

    private static void InTransaction(SqliteConnection db, Action<SqliteTransaction> write)
    {
        using var transaction = db.BeginTransaction(deferred: false);
        write(transaction);
        transaction.Commit();
    }

    private static ParsedFile Parse(PageSettings settings, DiskFile file, string hash, long hashedAt, byte[]? content, out string? bodyError)
    {
        bodyError = null;
        if (!Workspace.IsMarkdown(file.Path))
        {
            return new ParsedFile(new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "other", null, null), [], [], [], null);
        }
        var page = Page.Parse(file.Path, content!, settings);
        bodyError = page.BodyError;
        return new ParsedFile(
            new FileRow(file.Path, file.Mtime, file.Size, hash, hashedAt, "markdown", page.Frontmatter.Json, page.Frontmatter.Error),
            page.Links.Select(l => new LinkRow(file.Path, hash, l.Line, l.Kind, l.Type, l.Raw, l.Target)).ToList(),
            page.Findings.Select(f => new FindingRow(file.Path, hash, f.Rule, f.Line, f.Message)).ToList(),
            page.Entries.Select(e => new EntryRow(file.Path, hash, e.Line, e.Target, e.Description)).ToList(),
            new SearchRow(file.Path, hash, page.Title, PlainText.Collapse(page.Body)));
    }
}
