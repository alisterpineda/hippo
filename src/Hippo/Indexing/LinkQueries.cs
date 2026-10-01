using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>A link out of a file. <see cref="Type"/> is <c>file</c>, <c>directory</c>, <c>missing</c>, <c>url</c> or
/// <c>anchor</c>.</summary>
internal sealed record LinkOut(long Line, string Kind, string Type, string Raw, string? Target);

/// <summary>A link into a path, from <see cref="Source"/>.</summary>
internal sealed record LinkIn(string Source, long Line, string Kind, string Raw);

/// <summary>A path link whose <see cref="Target"/> is neither an indexed file nor a folder holding one; null when it
/// leaves the workspace.</summary>
internal sealed record BrokenLink(string Source, long Line, string Kind, string Raw, string? Target);

/// <summary>
/// Questions about the link graph. Whether a path link reaches a file is decided here, against the files indexed now, so
/// a target that comes or goes changes the answer without its linking pages being parsed again. A path link reaches a
/// folder when an indexed file lies under it: its path sorts after <c>target/</c> and before <c>target0</c>, <c>0</c> being
/// the character after <c>/</c>, which the index on <c>files.path</c> answers. The workspace root, <c>""</c>, holds every
/// file, the linking page among them.
/// </summary>
internal static class LinkQueries
{
    /// <summary>What the path <c>l.target</c> reaches: <c>file</c>, <c>directory</c> or <c>missing</c>. A query over
    /// another table of paths, such as <c>okf-index</c>'s over index entries, names that table <c>l</c> to share it, so
    /// the two agree on what is missing.</summary>
    internal const string PathTypeSql = """
        CASE
            WHEN EXISTS (SELECT 1 FROM files t WHERE t.path = l.target) THEN 'file'
            WHEN l.target = '' OR EXISTS (SELECT 1 FROM files t WHERE t.path > l.target || '/' AND t.path < l.target || '0')
                THEN 'directory'
            ELSE 'missing'
        END
        """;

    /// <summary>The type of link <c>l</c>: its stored type, or for a path link <see cref="PathTypeSql"/>.
    /// <see cref="Refs"/> reports it and <see cref="Broken"/> filters on it, so the two agree.</summary>
    private const string TypeSql = """
        CASE WHEN l.type != 'path' THEN l.type ELSE (
        """ + PathTypeSql + """
        ) END
        """;

    public static List<LinkOut> Refs(SqliteConnection db, string path) =>
        db.Query<LinkOut>("""
            SELECT l.line, l.kind, (
            """ + TypeSql + """
            ) AS Type, l.raw, l.target
            FROM links l
            JOIN files source ON source.id = l.source_id
            WHERE source.path = @path
            ORDER BY l.line, l.id
            """, new { path }).ToList();

    public static List<LinkIn> Backrefs(SqliteConnection db, string path, string? kind) =>
        db.Query<LinkIn>("""
            SELECT source.path AS Source, l.line, l.kind, l.raw
            FROM links l
            JOIN files source ON source.id = l.source_id
            WHERE l.target = @path AND (@kind IS NULL OR l.kind = @kind)
            ORDER BY source.path, l.line, l.id
            """, new { path, kind }).ToList();

    /// <summary>Every file that reaches <paramref name="path"/> through a chain of links of <paramref name="kind"/>
    /// (any kind when null), the path itself left out. <c>UNION</c> drops a file already reached, so a cycle ends.</summary>
    public static List<string> TransitiveBackrefs(SqliteConnection db, string path, string? kind) =>
        db.Query<string>("""
            WITH RECURSIVE reach (path) AS (
                SELECT @path
                UNION
                SELECT source.path
                FROM reach
                JOIN links l ON l.target = reach.path
                JOIN files source ON source.id = l.source_id
                WHERE @kind IS NULL OR l.kind = @kind
            )
            SELECT path FROM reach WHERE path != @path ORDER BY path
            """, new { path, kind }).ToList();

    public static List<BrokenLink> Broken(SqliteConnection db) =>
        db.Query<BrokenLink>("""
            SELECT source.path AS Source, l.line, l.kind, l.raw, l.target
            FROM links l
            JOIN files source ON source.id = l.source_id
            WHERE l.type = 'path' AND (
            """ + TypeSql + """
            ) = 'missing'
            ORDER BY source.path, l.line, l.id
            """).ToList();

    /// <summary>Indexed files that no link from another file targets. A page's links to itself do not count.</summary>
    public static HashSet<string> WithoutBackrefs(SqliteConnection db, SqliteTransaction? transaction) =>
        db.Query<string>("""
            SELECT f.path
            FROM files f
            WHERE NOT EXISTS (SELECT 1 FROM links l WHERE l.target = f.path AND l.source_id != f.id)
            """, transaction: transaction).ToHashSet(StringComparer.Ordinal);

    /// <summary>Indexed files with no link to another indexed file. A page's links to itself, and its links that are
    /// folders, URLs, anchors or missing, do not count.</summary>
    public static HashSet<string> WithoutRefs(SqliteConnection db, SqliteTransaction? transaction) =>
        db.Query<string>("""
            SELECT f.path
            FROM files f
            WHERE NOT EXISTS (SELECT 1 FROM links l JOIN files t ON t.path = l.target WHERE l.source_id = f.id AND t.id != f.id)
            """, transaction: transaction).ToHashSet(StringComparer.Ordinal);
}
