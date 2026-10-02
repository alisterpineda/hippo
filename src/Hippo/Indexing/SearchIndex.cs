using Dapper;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Indexing;

/// <summary>A page that matches a search, by rank, with its row in <c>files</c>. <see cref="Id"/> is its row in the search
/// table, valid only within the transaction it was read in.</summary>
internal sealed record SearchMatch(long Id, string Path, string Kind, long Size, long Mtime, string? ParseError, string? Frontmatter)
    : IFileRow;

/// <summary>A matching page's title, null when it has none, and the stretch of its body around the match.</summary>
internal sealed record SearchText(string? Title, string Snippet);

/// <summary>A search as <see cref="SearchIndex.Query"/> reads it: <see cref="Match"/> is its FTS5 query, and
/// <see cref="CanMatch"/> is false when no page can hold every word under <see cref="Tokenizer"/>.</summary>
internal sealed record SearchQuery(string Match, SearchTokenizer Tokenizer, bool CanMatch);

/// <summary>
/// The <c>search</c> table: an FTS5 table holding its own copy of each page's title, path and body, each row's rowid
/// the id of its page's row in <c>files</c>, so the sweep finds a page's row by rowid rather than by scanning the
/// table. No foreign key ties them: the sweep writes it directly, and no trigger does. The body has each run of
/// whitespace collapsed to one space, so a phrase matches across a line break. Results rank by BM25 with the title
/// weighted above the path, and the path above the body.
/// </summary>
internal static class SearchIndex
{
    /// <summary>The table's columns, in order. <see cref="Weights"/> and <see cref="BodyColumn"/> follow it, and so do
    /// the values the sweep inserts.</summary>
    internal const string Columns = "title, path, body";

    /// <summary>Each column's BM25 weight, in the order of <see cref="Columns"/>: the title above the path, the path
    /// above the body.</summary>
    private const string Weights = "10.0, 5.0, 1.0";

    /// <summary>The body's place in <see cref="Columns"/>, counting from 0.</summary>
    private const int BodyColumn = 2;

    /// <summary>The title of the page in a query's <c>search</c> row, null when it has none: the sweep stores a page
    /// with no title as <c>""</c>.</summary>
    internal const string Title = "NULLIF(search.title, '')";

    /// <summary>The table as a migration or <see cref="UseTokenizer"/> creates it. The tokenize clause is how
    /// <see cref="UseTokenizer"/> tells which tokenizer the table has.</summary>
    internal static string Create(SearchTokenizer tokenizer) =>
        $"CREATE VIRTUAL TABLE search USING fts5({Columns}, {TokenizeClause(tokenizer)})";

    private static string TokenizeClause(SearchTokenizer tokenizer) => tokenizer switch
    {
        SearchTokenizer.Trigram => "tokenize = 'trigram'",
        _ => "tokenize = 'porter unicode61'",
    };

    /// <summary>Recreates the search table under <paramref name="tokenizer"/> when it was made under another, copying
    /// every row across, rowid included, so no page needs reading again. A no-op when it already uses that tokenizer.</summary>
    public static void UseTokenizer(SqliteConnection db, SearchTokenizer tokenizer)
    {
        if (Uses(db, null, tokenizer))
        {
            return;
        }
        using var transaction = db.BeginTransaction(deferred: false);
        // Another process may have recreated it while this one waited for the lock.
        if (Uses(db, transaction, tokenizer))
        {
            return;
        }
        Execute(db, transaction, $"CREATE TEMP TABLE search_copy AS SELECT rowid AS id, {Columns} FROM search");
        Execute(db, transaction, "DROP TABLE search");
        Execute(db, transaction, Create(tokenizer));
        Execute(db, transaction, $"INSERT INTO search (rowid, {Columns}) SELECT id, {Columns} FROM temp.search_copy");
        Execute(db, transaction, "DROP TABLE temp.search_copy");
        transaction.Commit();
    }

    private static bool Uses(SqliteConnection db, SqliteTransaction? transaction, SearchTokenizer tokenizer)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'search'";
        return command.ExecuteScalar() is string sql && sql.Contains(TokenizeClause(tokenizer), StringComparison.Ordinal);
    }

    private static void Execute(SqliteConnection db, SqliteTransaction transaction, string sql)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// <paramref name="text"/> as a search under <paramref name="tokenizer"/> in which every phrase, the text between a
    /// pair of <c>"</c>, and every whitespace-separated word outside them must appear; null when there are neither. Every
    /// <c>"</c> is syntax, so an odd number of them is an error, and an empty phrase is left out. Each word and phrase
    /// is quoted for FTS5, so punctuation in it is matched as text the tokenizer splits, never read as query syntax. A
    /// phrase is an FTS5 phrase: its words side by side and in order under <c>porter</c>, its text as a substring under
    /// <c>trigram</c>. Its whitespace is collapsed, as the sweep collapses the body's. A word or phrase shorter than
    /// three characters has no trigram, and FTS5 drops it from the query rather than requiring it, so under
    /// <see cref="SearchTokenizer.Trigram"/> a search holding one can match nothing.
    /// </summary>
    public static SearchQuery? Query(string text, SearchTokenizer tokenizer)
    {
        var parts = text.Split('"');
        if (parts.Length % 2 == 0)
        {
            throw new HippoException("unclosed quote in query");
        }
        // The parts alternate between text outside quotes, split into words, and a phrase, starting outside.
        var terms = parts.SelectMany((part, i) => i % 2 == 0
                ? part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : [PlainText.CollapseRuns(part)])
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToList();
        if (terms.Count == 0)
        {
            return null;
        }
        var match = string.Join(' ', terms.Select(term => $"\"{term}\""));
        var canMatch = tokenizer != SearchTokenizer.Trigram || terms.All(term => term.EnumerateRunes().Count() >= 3);
        return new SearchQuery(match, tokenizer, canMatch);
    }

    private const string MatchSql = $"""
        SELECT search.rowid AS Id, {FileQueries.Columns}
        FROM search JOIN files ON files.id = search.rowid
        WHERE search MATCH @Match
        """;

    private const string RankSql = $"""

        ORDER BY bm25(search, {Weights}), files.path
        """;

    /// <summary>Every page matching <paramref name="query"/>, best first, each with its frontmatter when
    /// <paramref name="frontmatter"/> is set.</summary>
    public static List<SearchMatch> Matches(SqliteConnection db, SqliteTransaction transaction, SearchQuery query, bool frontmatter) =>
        query.CanMatch ? db.Query<SearchMatch>(MatchSql + RankSql, new { query.Match, frontmatter }, transaction).ToList() : [];

    /// <summary>What <see cref="Text"/> has <c>snippet()</c> put where it cuts the body, so a cut is told apart from
    /// <c>...</c> the page itself holds. A control character no page has reason to hold.</summary>
    private const string Cut = "\u0001";

    /// <summary>The title of the page at <paramref name="id"/>, from <see cref="Matches"/> in the same transaction, and
    /// its body around the match on one line, each matched term marked <c>**</c> as markdown bold.</summary>
    private const string TextSql = $"""
        SELECT {Title} AS Title, snippet(search, @Column, '**', '**', @Cut, @Tokens) AS Snippet
        FROM search
        WHERE search MATCH @Match AND rowid = @Id
        """;

    public static SearchText Text(SqliteConnection db, SqliteTransaction transaction, SearchQuery query, long id)
    {
        // The snippet runs to about 20 tokens: 20 words under porter. A trigram token is one character's three-character
        // window, so trigram takes FTS5's most, 64, about that many characters.
        var trigram = query.Tokenizer == SearchTokenizer.Trigram;
        var text = db.QuerySingle<SearchText>(TextSql, new { query.Match, Id = id, Column = BodyColumn, Cut, Tokens = trigram ? 64 : 20 }, transaction);
        var snippet = PlainText.Collapse(text.Snippet);
        return text with { Snippet = (trigram ? WholeWords(snippet) : snippet).Replace(Cut, "...", StringComparison.Ordinal) };
    }

    /// <summary>A trigram snippet less the words its cuts go through. A trigram snippet starts and ends at any
    /// character, so the word beside a cut is taken as cut, unless it holds a match.</summary>
    private static string WholeWords(string snippet)
    {
        if (snippet.StartsWith(Cut, StringComparison.Ordinal) && snippet.IndexOf(' ', StringComparison.Ordinal) is > 0 and var first
            && !snippet.AsSpan(0, first).Contains("**", StringComparison.Ordinal))
        {
            snippet = Cut + snippet[(first + 1)..];
        }
        if (snippet.EndsWith(Cut, StringComparison.Ordinal) && snippet.LastIndexOf(' ') is var last && last > Cut.Length
            && !snippet.AsSpan(last).Contains("**", StringComparison.Ordinal))
        {
            snippet = snippet[..last] + Cut;
        }
        return snippet;
    }
}
