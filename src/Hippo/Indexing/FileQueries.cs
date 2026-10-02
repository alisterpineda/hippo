using Dapper;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>The fields of a <c>files</c> row that a listing and a search match both carry, for code that filters
/// either.</summary>
internal interface IFileRow
{
    string Path { get; }
    string Kind { get; }
    string? ParseError { get; }

    /// <summary>The file's frontmatter as JSON, null when it has none or it failed to parse.</summary>
    string? Frontmatter { get; }
}

/// <summary>An indexed file. <see cref="Title"/> is its page's title, null when it is not a page, the page has none,
/// or the listing was read without titles.</summary>
internal sealed record FileListing(string Path, string Kind, long Size, long Mtime, string? ParseError, string? Frontmatter, string? Title)
    : IFileRow;

internal sealed record FileDetail(string Path, string Kind, long Size, long Mtime, string Hash, string? Frontmatter, string? ParseError);

internal sealed record FileCounts(long Total, long Markdown, long Other, long ParseErrors);

internal sealed record FileParseError(string Path, string ParseError);

internal static class FileQueries
{
    /// <summary>The columns of a <c>files</c> row that a listing and a search match both carry, named for their
    /// records. The frontmatter is the widest of them and only filters and fields read it, so it is read only when the
    /// query's <c>@frontmatter</c> parameter is set, and is null otherwise.</summary>
    internal const string Columns =
        "files.path AS Path, files.kind AS Kind, files.size AS Size, files.mtime AS Mtime, files.parse_error AS ParseError, "
        + "CASE WHEN @frontmatter THEN files.frontmatter END AS Frontmatter";

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

    private const string OrderSql = "\nORDER BY files.path";

    /// <summary>Every indexed file in path order, each with its title when <paramref name="titles"/> is set and its
    /// frontmatter when <paramref name="frontmatter"/> is.</summary>
    public static List<FileListing> List(
        SqliteConnection db, bool titles = true, bool frontmatter = true, SqliteTransaction? transaction = null) =>
        titles
            ? db.Query<FileListing>(TitledListSql + OrderSql, new { frontmatter }, transaction).ToList()
            : db.Query<FileListing>(UntitledListSql + OrderSql, new { frontmatter }, transaction).ToList();

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
