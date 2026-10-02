using System.Text;
using System.Text.Json;
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

    /// <summary>Whether link <c>l</c>, from the file <c>source</c>, is of the kind <c>@kind</c> (any kind when null) and
    /// comes from one of the paths in the JSON array <c>@sources</c> (any file when null), which
    /// <see cref="JsonArray"/> writes. SQLite reads the array once per query, not once per link.</summary>
    private const string LinkFilterSql = """
        (@kind IS NULL OR l.kind = @kind)
        AND (@sources IS NULL OR source.path IN (SELECT value FROM json_each(@sources)))
        """;

    /// <summary>The links into <paramref name="path"/>, only those of <paramref name="kind"/> when it is given, and only
    /// those from <paramref name="sources"/> when they are.</summary>
    public static List<LinkIn> Backrefs(SqliteConnection db, string path, string? kind, IReadOnlyCollection<string>? sources) =>
        db.Query<LinkIn>("""
            SELECT source.path AS Source, l.line, l.kind, l.raw
            FROM links l
            JOIN files source ON source.id = l.source_id
            WHERE l.target = @path AND (
            """ + LinkFilterSql + """
            )
            ORDER BY source.path, l.line, l.id
            """, new { path, kind, sources = JsonArray(sources) }).ToList();

    /// <summary>Every file that reaches <paramref name="path"/> through a chain of links of <paramref name="kind"/>
    /// (any kind when null) passing only through <paramref name="sources"/> (any file when null), the path itself left
    /// out. <c>UNION</c> drops a file already reached, so a cycle ends.</summary>
    public static List<string> TransitiveBackrefs(SqliteConnection db, string path, string? kind, IReadOnlyCollection<string>? sources) =>
        db.Query<string>("""
            WITH RECURSIVE reach (path) AS (
                SELECT @path
                UNION
                SELECT source.path
                FROM reach
                JOIN links l ON l.target = reach.path
                JOIN files source ON source.id = l.source_id
                WHERE (
            """ + LinkFilterSql + """
            ))
            SELECT path FROM reach WHERE path != @path ORDER BY path
            """, new { path, kind, sources = JsonArray(sources) }).ToList();

    /// <summary>Every file with a link out: the files a filter on a link's source can pick from.</summary>
    public static List<string> Sources(SqliteConnection db, SqliteTransaction? transaction) =>
        db.Query<string>(
            "SELECT path FROM files f WHERE EXISTS (SELECT 1 FROM links l WHERE l.source_id = f.id) ORDER BY path",
            transaction: transaction).ToList();

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

    /// <summary>Indexed files that no link from another file targets, counting only links of <paramref name="kind"/>
    /// when it is given, and only those from <paramref name="sources"/> when they are. A page's links to itself do not
    /// count.</summary>
    public static HashSet<string> WithoutBackrefs(
        SqliteConnection db, SqliteTransaction? transaction, string? kind, IReadOnlyCollection<string>? sources) =>
        db.Query<string>("""
            SELECT f.path
            FROM files f
            WHERE NOT EXISTS (
                SELECT 1
                FROM links l
                JOIN files source ON source.id = l.source_id
                WHERE l.target = f.path AND l.source_id != f.id AND (
            """ + LinkFilterSql + """
            ))
            """, new { kind, sources = JsonArray(sources) }, transaction).ToHashSet(StringComparer.Ordinal);

    /// <summary>Indexed files with no link to another indexed file, counting only links of <paramref name="kind"/> when
    /// it is given. A page's links to itself, and its links that are folders, URLs, anchors or missing, do not
    /// count.</summary>
    public static HashSet<string> WithoutRefs(SqliteConnection db, SqliteTransaction? transaction, string? kind) =>
        db.Query<string>("""
            SELECT f.path
            FROM files f
            WHERE NOT EXISTS (
                SELECT 1
                FROM links l JOIN files t ON t.path = l.target
                WHERE l.source_id = f.id AND t.id != f.id AND (@kind IS NULL OR l.kind = @kind)
            )
            """, new { kind }, transaction).ToHashSet(StringComparer.Ordinal);

    /// <summary>The paths <paramref name="paths"/> as the JSON array <see cref="LinkFilterSql"/> reads, or null for
    /// any source.</summary>
    private static string? JsonArray(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            return null;
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var path in paths)
            {
                writer.WriteStringValue(path);
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
