using Dapper;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Okf;

/// <summary>An <c>okf-index</c> finding on the file at <see cref="Path"/>, concerning the files at
/// <see cref="Related"/>.</summary>
internal sealed record IndexFinding(string Path, long? Line, string Message, List<string> Related);

/// <summary>
/// The <c>okf-index</c> rule: where an OKF bundle's <c>index.md</c> files disagree with the pages they list (§8). Each
/// finding concerns an index and a page, and either can change without the other, so the findings are worked out from
/// the index when asked rather than stored when a file is parsed. An index covers the pages in its own folder and every
/// folder below it, up to its bundle's root. It reports:
/// <list type="bullet">
/// <item>an entry whose page is missing;</item>
/// <item>an entry whose description is not its page's <c>description</c>, compared as written with each run of
/// whitespace read as one space. An entry for a reserved file, a file that is not markdown, or a page whose frontmatter
/// cannot be parsed is not compared;</item>
/// <item>a page that no index covering it links to, in an entry or anywhere else. A bundle is OKF only when its root
/// has an <c>index.md</c>, so every page has one covering it.</item>
/// </list>
/// </summary>
internal static class IndexSync
{
    // Internal, not private: the code Dapper.AOT generates must reach these types.
    internal sealed record EntryRow(string Index, long Line, string Target, string? Description, string State, bool Compared, string? PageDescription);

    public static List<IndexFinding> Check(SqliteConnection db, IReadOnlyList<string> okfBundles, IReadOnlyList<string> bundles)
    {
        var findings = new List<IndexFinding>();
        CheckEntries(db, findings);
        CheckListed(db, findings, okfBundles, bundles);
        return findings;
    }

    /// <summary>Only an <c>index.md</c> in an OKF bundle has entries, so every entry is checked. A target is missing
    /// when it is neither a file nor a folder holding one, as <see cref="LinkQueries.PathTypeSql"/> decides for a link,
    /// which is why the entries are named <c>l</c>. Its page is the file whose path is equal to the target under NFC,
    /// the one named exactly as the target when two are, the rule <see cref="FileQueries.Get"/> follows: a change to
    /// one belongs in both.</summary>
    private static void CheckEntries(SqliteConnection db, List<IndexFinding> findings)
    {
        var entries = db.Query<EntryRow>("""
            SELECT
                f.path AS "Index", l.line, l.target, l.description, (
            """ + LinkQueries.PathTypeSql + """
                ) AS State,
                t.kind IS 'markdown' AND t.parse_error IS NULL AS Compared,
                CASE json_type(t.frontmatter, '$.description')
                    WHEN 'text' THEN t.frontmatter ->> '$.description'
                    WHEN 'null' THEN NULL
                    ELSE t.frontmatter -> '$.description'
                END AS PageDescription
            FROM index_entries l
            JOIN files f ON f.id = l.file_id
            LEFT JOIN files t ON t.id = (
                SELECT id FROM files WHERE path_nfd = l.target_nfd ORDER BY path = l.target DESC, path LIMIT 1)
            ORDER BY f.path, l.line, l.id
            """);
        foreach (var entry in entries)
        {
            if (entry.State == "missing")
            {
                findings.Add(new(entry.Index, entry.Line, $"entry links to {entry.Target}, which does not exist", [entry.Target]));
                continue;
            }
            if (!entry.Compared || OkfBundle.IsReserved(entry.Target))
            {
                continue;
            }
            var page = entry.PageDescription is { } text && IndexEntries.Normalize(text) is { Length: > 0 } normalized ? normalized : null;
            var message = (entry.Description, page) switch
            {
                (null, null) => null,
                (null, _) => $"entry for {entry.Target} has no description; the page's is '{page}'",
                (_, null) => $"entry for {entry.Target} has a description, but the page has none",
                _ when entry.Description != page => $"entry for {entry.Target} does not match the page's description '{page}'",
                _ => null,
            };
            if (message is not null)
            {
                findings.Add(new(entry.Index, entry.Line, message, [entry.Target]));
            }
        }
    }

    /// <summary>Reports each page in an OKF bundle that none of the indexes covering it links to, in any form equal to its
    /// path under NFC.
    /// A page belongs to the deepest bundle holding it, as its links do, so the indexes of an OKF bundle around a
    /// nested bundle do not cover the nested bundle's pages.</summary>
    private static void CheckListed(SqliteConnection db, List<IndexFinding> findings, IReadOnlyList<string> okfBundles, IReadOnlyList<string> bundles)
    {
        var pages = new List<(string Path, string Bundle)>();
        foreach (var root in okfBundles)
        {
            pages.AddRange(db.Query<string>("""
                    SELECT path FROM files
                    WHERE kind = 'markdown' AND path > @root || '/' AND path < @root || '0'
                    ORDER BY path
                    """, new { root })
                .Where(path => Page.BundleRoot(path, bundles) == root)
                .Select(path => (path, root)));
        }
        var indexes = pages.Select(page => page.Path).Where(IndexEntries.IsIndex).ToHashSet(StringComparer.Ordinal);
        // One query per index rather than one over every link, so the links read are the indexes' own.
        var linked = new HashSet<(string Index, string TargetNfd)>();
        foreach (var index in indexes)
        {
            linked.UnionWith(db.Query<string>("""
                    SELECT l.target_nfd
                    FROM files s
                    JOIN links l ON l.source_id = s.id
                    WHERE s.path = @index AND l.target_nfd IS NOT NULL
                    """, new { index })
                .Select(target => (index, target)));
        }

        foreach (var (path, bundle) in pages)
        {
            if (OkfBundle.IsReserved(path))
            {
                continue;
            }
            var covering = new List<string>();
            for (var folder = path[..path.LastIndexOf('/')]; ; folder = folder[..folder.LastIndexOf('/')])
            {
                var index = OkfBundle.IndexOf(folder);
                if (indexes.Contains(index))
                {
                    covering.Add(index);
                }
                if (folder == bundle)
                {
                    break;
                }
            }
            var pathNfd = Nfd.Of(path);
            if (!covering.Any(index => linked.Contains((index, pathNfd))))
            {
                findings.Add(new(path, null, $"no index above it links to it: {string.Join(", ", covering)}", covering));
            }
        }
    }
}
