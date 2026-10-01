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

/// <summary>The fields of a <c>files</c> row that a listing and a search match both carry, for code that filters
/// either.</summary>
internal interface IFileRow
{
    string Path { get; }
    string Kind { get; }
    string? ParseError { get; }
}

/// <summary>An indexed file. <see cref="Title"/> is its page's title, null when it is not a page, the page has none,
/// or the listing was read without titles.</summary>
internal sealed record FileListing(string Path, string Kind, long Size, long Mtime, string? ParseError, string? Title) : IFileRow;

internal sealed record FileDetail(string Path, string Kind, long Size, long Mtime, string Hash, string? Frontmatter, string? ParseError);

internal sealed record FileCounts(long Total, long Markdown, long Other, long ParseErrors);

internal sealed record FileParseError(string Path, string ParseError);

internal static class FileQueries
{
    /// <summary>The columns of a <c>files</c> row that a listing and a search match both carry, named for their
    /// records.</summary>
    internal const string Columns =
        "files.path AS Path, files.kind AS Kind, files.size AS Size, files.mtime AS Mtime, files.parse_error AS ParseError";

    /// <summary>A page's title is in its row of the <c>search</c> table, whose rowid is the page's id. Reading it reads
    /// that whole row, body included, so a listing joins it only when titles are wanted.</summary>
    private const string TitledListSql = $"""
        SELECT {Columns}, {SearchIndex.Title} AS Title
        FROM files LEFT JOIN search ON search.rowid = files.id
        """;

    private const string UntitledListSql = $"""
        SELECT {Columns}, NULL AS Title
        FROM files
        """;

    private const string WhereSql = "\nWHERE " + FrontmatterFilter.Sql;

    private const string OrderSql = "\nORDER BY files.path";

    /// <summary>Every indexed file in path order, less those whose frontmatter fails <paramref name="where"/>, each with
    /// its title when <paramref name="titles"/> is set.</summary>
    public static List<FileListing> List(SqliteConnection db, FrontmatterFilter? where, bool titles = true, SqliteTransaction? transaction = null) =>
        (where, titles) switch
        {
            (null, true) => db.Query<FileListing>(TitledListSql + OrderSql, transaction: transaction).ToList(),
            (null, false) => db.Query<FileListing>(UntitledListSql + OrderSql, transaction: transaction).ToList(),
            ({ } filter, true) => db.Query<FileListing>(TitledListSql + WhereSql + OrderSql, new { filter.JsonPath, filter.Value }, transaction).ToList(),
            ({ } filter, false) => db.Query<FileListing>(UntitledListSql + WhereSql + OrderSql, new { filter.JsonPath, filter.Value }, transaction).ToList(),
        };

    public static FileDetail? Get(SqliteConnection db, string path) =>
        db.QuerySingleOrDefault<FileDetail>(
            "SELECT path, kind, size, mtime, hash, frontmatter, parse_error AS ParseError FROM files WHERE path = @path",
            new { path });

    /// <summary>Every file whose frontmatter failed to parse, in path order, with the error.</summary>
    public static List<FileParseError> ParseErrors(SqliteConnection db) =>
        db.Query<FileParseError>(
            "SELECT path, parse_error AS ParseError FROM files WHERE parse_error IS NOT NULL ORDER BY path").ToList();

    public static FileCounts Count(SqliteConnection db) =>
        db.QuerySingle<FileCounts>("""
            SELECT count(*) AS Total,
                   count(*) FILTER (WHERE kind = 'markdown') AS Markdown,
                   count(*) FILTER (WHERE kind = 'other') AS Other,
                   count(*) FILTER (WHERE parse_error IS NOT NULL) AS ParseErrors
            FROM files
            """);
}
