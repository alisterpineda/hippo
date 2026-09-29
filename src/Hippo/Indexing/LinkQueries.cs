using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>A link out of a file. <see cref="Type"/> is <c>file</c>, <c>missing</c>, <c>url</c> or <c>anchor</c>.</summary>
internal sealed record LinkOut(long Line, string Kind, string Type, string Raw, string? Target);

/// <summary>A link into a path, from <see cref="Source"/>.</summary>
internal sealed record LinkIn(string Source, long Line, string Kind, string Raw);

/// <summary>A path link whose <see cref="Target"/> is not an indexed file; null when it leaves the notebook, and
/// <c>""</c> when it is the notebook root.</summary>
internal sealed record BrokenLink(string Source, long Line, string Kind, string Raw, string? Target);

/// <summary>
/// Questions about the link graph. Whether a path link reaches a file is decided here, against the files indexed now, so
/// a target that comes or goes changes the answer without its linking pages being parsed again.
/// </summary>
internal static class LinkQueries
{
    public static List<LinkOut> Refs(SqliteConnection db, string path) =>
        db.Query<LinkOut>("""
            SELECT l.line, l.kind,
                   CASE WHEN l.type != 'path' THEN l.type WHEN target.id IS NULL THEN 'missing' ELSE 'file' END AS Type,
                   l.raw, l.target
            FROM links l
            JOIN files source ON source.id = l.source_id
            LEFT JOIN files target ON target.path = l.target
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
            WHERE l.type = 'path' AND NOT EXISTS (SELECT 1 FROM files target WHERE target.path = l.target)
            ORDER BY source.path, l.line, l.id
            """).ToList();

    /// <summary>Markdown files no other file links to. A page's links to itself do not count.</summary>
    public static List<string> Orphans(SqliteConnection db) =>
        db.Query<string>("""
            SELECT f.path
            FROM files f
            WHERE f.kind = 'markdown'
              AND NOT EXISTS (SELECT 1 FROM links l WHERE l.target = f.path AND l.source_id != f.id)
            ORDER BY f.path
            """).ToList();
}
