using Dapper;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>
/// Runs every query in <see cref="FileQueries"/> against a database built from the migration scripts. Together with
/// <see cref="SweeperTests"/>, which drives each of the sweep's statements, every Dapper query in hippo runs here once.
/// </summary>
public sealed class FileQueriesTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _db;

    public FileQueriesTests()
    {
        _db = _database.Open();
        Insert("wiki/a.md", "markdown", """{"type":"Topic","title":"A","tags":["x","y"],"count":3,"draft":true,"generated":{"at":"2026-09-01"}}""");
        Insert("wiki/b.md", "markdown", """{"type":"Person","title":"B","tags":"x"}""");
        Insert("raw/bad.md", "markdown", null, "line 2: bad");
        Insert("raw/c.png", "other", null);
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    private void Insert(string path, string kind, string? frontmatter, string? parseError = null) =>
        _db.Execute(
            "INSERT INTO files (path, mtime, size, hash, hashed_at, kind, frontmatter, parse_error) VALUES (@path, 0, 1, 'h', 0, @kind, @frontmatter, @parseError)",
            new { path, kind, frontmatter, parseError });

    [Fact]
    public void List_returns_every_file_in_path_order()
    {
        Assert.Equal(["raw/bad.md", "raw/c.png", "wiki/a.md", "wiki/b.md"], FileQueries.List(_db).Select(f => f.Path));
    }

    [Fact]
    public void List_reads_each_page_title_from_the_search_row_that_shares_its_id()
    {
        _db.Execute("INSERT INTO search (rowid, title, path, body) SELECT id, 'Alpha', path, '' FROM files WHERE path = 'wiki/a.md'");
        _db.Execute("INSERT INTO search (rowid, title, path, body) SELECT id, '', path, '' FROM files WHERE path = 'wiki/b.md'");

        Assert.Equal([null, null, "Alpha", null], FileQueries.List(_db).Select(f => f.Title));
    }

    [Fact]
    public void List_without_titles_leaves_every_title_null()
    {
        _db.Execute("INSERT INTO search (rowid, title, path, body) SELECT id, 'Alpha', path, '' FROM files WHERE path = 'wiki/a.md'");

        var files = FileQueries.List(_db, titles: false);

        Assert.Equal(["raw/bad.md", "raw/c.png", "wiki/a.md", "wiki/b.md"], files.Select(f => f.Path));
        Assert.All(files, f => Assert.Null(f.Title));
    }

    [Fact]
    public void List_carries_each_file_frontmatter_and_parse_error()
    {
        var files = FileQueries.List(_db).ToDictionary(f => f.Path);

        Assert.Equal("""{"type":"Person","title":"B","tags":"x"}""", files["wiki/b.md"].Frontmatter);
        Assert.Equal((null, "line 2: bad"), (files["raw/bad.md"].Frontmatter, files["raw/bad.md"].ParseError));
        Assert.Null(files["raw/c.png"].Frontmatter);
    }

    [Fact]
    public void List_without_frontmatter_leaves_every_frontmatter_null_and_keeps_parse_errors()
    {
        var files = FileQueries.List(_db, frontmatter: false).ToDictionary(f => f.Path);

        Assert.All(files.Values, f => Assert.Null(f.Frontmatter));
        Assert.Equal("line 2: bad", files["raw/bad.md"].ParseError);
    }

    [Fact]
    public void Get_returns_the_whole_row()
    {
        var file = FileQueries.Get(_db, "raw/bad.md");

        Assert.NotNull(file);
        Assert.Equal(("markdown", 1L, "h", null, "line 2: bad"), (file.Kind, file.Size, file.Hash, file.Frontmatter, file.ParseError));
    }

    [Fact]
    public void Get_returns_null_for_a_path_not_in_the_index()
    {
        Assert.Null(FileQueries.Get(_db, "nope.md"));
    }

    [Fact]
    public void Count_totals_files_by_kind_and_parse_errors()
    {
        Assert.Equal(new FileCounts(4, 3, 1, 1), FileQueries.Count(_db));
    }
}
