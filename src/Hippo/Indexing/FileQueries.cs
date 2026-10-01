using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>Matches files whose frontmatter <see cref="Field"/> (dotted for nested mappings) equals
/// <see cref="Value"/> as text, or, for a list, has an element that does.</summary>
internal sealed record FrontmatterFilter(string Field, string Value)
{
    /// <summary>Parses <c>field=value</c>, splitting at the first <c>=</c>.</summary>
    public static FrontmatterFilter Parse(string text)
    {
        var separator = text.IndexOf('=');
        var field = separator < 0 ? "" : text[..separator];
        if (field.Length == 0 || field.Split('.').Any(part => part.Length == 0 || part.Contains('"')))
        {
            throw new HippoException($"--where expects <field>=<value>, such as type=Topic or generated.at=2026-09-01; got '{text}'");
        }
        return new FrontmatterFilter(field, text[(separator + 1)..]);
    }

    public string JsonPath => "$" + string.Concat(Field.Split('.').Select(part => $".\"{part}\""));

    /// <summary>The condition a <c>files</c> row meets when its frontmatter matches, given <see cref="JsonPath"/> and
    /// <see cref="Value"/> as <c>@JsonPath</c> and <c>@Value</c>.</summary>
    public const string Sql = """
        -- json_each walks a mapping's members too; a mapping itself never equals a value.
        json_type(files.frontmatter, @JsonPath) != 'object'
          AND EXISTS (
            SELECT 1 FROM json_each(files.frontmatter, @JsonPath) AS field
            WHERE field.type NOT IN ('object', 'array')
              AND CASE field.type WHEN 'true' THEN 'true' WHEN 'false' THEN 'false' WHEN 'null' THEN 'null'
                  ELSE CAST(field.value AS TEXT) END = @Value)
        """;
}

internal sealed record FileListing(string Path, string Kind, long Size, long Mtime, string? ParseError);

internal sealed record FileDetail(string Path, string Kind, long Size, long Mtime, string Hash, string? Frontmatter, string? ParseError);

internal sealed record FileCounts(long Total, long Markdown, long Other, long ParseErrors);

internal static class FileQueries
{
    public static List<FileListing> List(SqliteConnection db, FrontmatterFilter? where) =>
        where is null
            ? db.Query<FileListing>("SELECT path, kind, size, mtime, parse_error AS ParseError FROM files ORDER BY path").ToList()
            : db.Query<FileListing>("""
                SELECT path, kind, size, mtime, parse_error AS ParseError
                FROM files
                WHERE
                """ + FrontmatterFilter.Sql + """

                ORDER BY path
                """, new { where.JsonPath, where.Value }).ToList();

    public static FileDetail? Get(SqliteConnection db, string path) =>
        db.QuerySingleOrDefault<FileDetail>(
            "SELECT path, kind, size, mtime, hash, frontmatter, parse_error AS ParseError FROM files WHERE path = @path",
            new { path });

    public static FileCounts Count(SqliteConnection db) =>
        db.QuerySingle<FileCounts>("""
            SELECT count(*) AS Total,
                   count(*) FILTER (WHERE kind = 'markdown') AS Markdown,
                   count(*) FILTER (WHERE kind = 'other') AS Other,
                   count(*) FILTER (WHERE parse_error IS NOT NULL) AS ParseErrors
            FROM files
            """);
}
