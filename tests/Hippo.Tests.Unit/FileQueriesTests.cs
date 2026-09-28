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
        Insert("raw/c.png", "plain", null);
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

    private List<string> Where(string field, string value) =>
        FileQueries.List(_db, new FrontmatterFilter(field, value)).Select(f => f.Path).ToList();

    [Fact]
    public void List_returns_every_file_in_path_order()
    {
        Assert.Equal(["raw/bad.md", "raw/c.png", "wiki/a.md", "wiki/b.md"], FileQueries.List(_db, null).Select(f => f.Path));
    }

    [Fact]
    public void Where_matches_a_string_field()
    {
        Assert.Equal(["wiki/a.md"], Where("type", "Topic"));
    }

    [Fact]
    public void Where_matches_numbers_and_booleans_by_their_text()
    {
        Assert.Equal(["wiki/a.md"], Where("count", "3"));
        Assert.Equal(["wiki/a.md"], Where("draft", "true"));
    }

    [Fact]
    public void Where_matches_an_element_of_a_list()
    {
        Assert.Equal(["wiki/a.md", "wiki/b.md"], Where("tags", "x"));
        Assert.Equal(["wiki/a.md"], Where("tags", "y"));
    }

    [Fact]
    public void Where_follows_dotted_fields_into_nested_mappings()
    {
        Assert.Equal(["wiki/a.md"], Where("generated.at", "2026-09-01"));
    }

    [Fact]
    public void Where_on_a_mapping_field_matches_nothing()
    {
        Assert.Empty(Where("generated", "2026-09-01"));
    }

    [Fact]
    public void Where_on_a_missing_field_matches_nothing()
    {
        Assert.Empty(Where("status", "draft"));
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

    [Theory]
    [InlineData("type=Topic", "type", "Topic")]
    [InlineData("title=a=b", "title", "a=b")]
    [InlineData("status=", "status", "")]
    public void A_where_filter_splits_at_the_first_equals_sign(string text, string field, string value)
    {
        Assert.Equal(new FrontmatterFilter(field, value), FrontmatterFilter.Parse(text));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("=Topic")]
    [InlineData("a..b=c")]
    public void A_where_filter_without_a_field_is_an_error(string text)
    {
        Assert.Throws<HippoException>(() => FrontmatterFilter.Parse(text));
    }
}
