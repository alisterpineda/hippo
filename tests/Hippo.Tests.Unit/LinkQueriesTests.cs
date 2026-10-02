using Dapper;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>Runs the queries in <see cref="LinkQueries"/> against a database built from the migration scripts, over a
/// small graph with a cycle: index → a → b ⇢ c → a (⇢ is a frontmatter link). <see cref="LinkQueries.WithoutRefs"/> and
/// <see cref="LinkQueries.WithoutBackrefs"/> are tested through <c>find</c>, in <c>FindCommandTests</c>.</summary>
public sealed class LinkQueriesTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _db;

    public LinkQueriesTests()
    {
        _db = _database.Open();
        foreach (var path in new[] { "wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/index.md", "wiki/self.md", "raw/x.md" })
        {
            AddFile(path, "markdown");
        }
        AddFile("img.png", "other");

        Link("wiki/a.md", 3, "body", "path", "b.md", "wiki/b.md", "B");
        Link("wiki/a.md", 4, "body", "path", "../raw/gone.md", "raw/gone.md", "Gone");
        Link("wiki/a.md", 5, "body", "url", "https://example.com", null, "the web");
        Link("wiki/a.md", 6, "body", "anchor", "#top", null, "");
        Link("wiki/a.md", 7, "body", "path", "../../x.md", null, "outside");
        Link("wiki/b.md", 2, "frontmatter", "path", "c.md", "wiki/c.md", null);
        Link("wiki/c.md", 1, "body", "path", "a.md", "wiki/a.md", "A");
        Link("wiki/index.md", 1, "body", "path", "a.md", "wiki/a.md", "Page A");
        Link("wiki/self.md", 1, "body", "path", "self.md", "wiki/self.md", "self");
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    private void AddFile(string path, string kind) =>
        _db.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES (@path, 0, 1, 'h', 0, @kind)", new { path, kind });

    private void Link(string source, int line, string kind, string type, string raw, string? target, string? text) =>
        _db.Execute("""
            INSERT INTO links (source_id, line, kind, type, raw, target, text)
            SELECT id, @line, @kind, @type, @raw, @target, @text FROM files WHERE path = @source
            """, new { source, line, kind, type, raw, target, text });

    [Fact]
    public void Refs_lists_a_files_links_in_line_order_with_their_class_and_text()
    {
        Assert.Equal(
            [
                new LinkOut(3, "body", "file", "b.md", "wiki/b.md", "B"),
                new LinkOut(4, "body", "missing", "../raw/gone.md", "raw/gone.md", "Gone"),
                new LinkOut(5, "body", "url", "https://example.com", null, "the web"),
                new LinkOut(6, "body", "anchor", "#top", null, ""),
                new LinkOut(7, "body", "missing", "../../x.md", null, "outside"),
            ],
            LinkQueries.Refs(_db, "wiki/a.md"));
    }

    [Fact]
    public void Refs_of_a_file_without_links_is_empty()
    {
        Assert.Empty(LinkQueries.Refs(_db, "img.png"));
    }

    [Fact]
    public void Backrefs_lists_the_links_into_a_path_with_their_text()
    {
        Assert.Equal(
            [new LinkIn("wiki/c.md", 1, "body", "a.md", "A"), new LinkIn("wiki/index.md", 1, "body", "a.md", "Page A")],
            LinkQueries.Backrefs(_db, "wiki/a.md", null, null));
    }

    [Fact]
    public void Backrefs_can_be_limited_to_one_kind()
    {
        Assert.Equal([new LinkIn("wiki/b.md", 2, "frontmatter", "c.md", null)], LinkQueries.Backrefs(_db, "wiki/c.md", "frontmatter", null));
        Assert.Empty(LinkQueries.Backrefs(_db, "wiki/c.md", "body", null));
    }

    [Fact]
    public void Backrefs_can_be_limited_to_links_from_some_files()
    {
        Assert.Equal([new LinkIn("wiki/index.md", 1, "body", "a.md", "Page A")], LinkQueries.Backrefs(_db, "wiki/a.md", null, ["wiki/index.md", "raw/x.md"]));
        Assert.Empty(LinkQueries.Backrefs(_db, "wiki/a.md", null, []));
    }

    [Fact]
    public void Backrefs_of_a_missing_path_lists_the_links_that_break_on_it()
    {
        Assert.Equal([new LinkIn("wiki/a.md", 4, "body", "../raw/gone.md", "Gone")], LinkQueries.Backrefs(_db, "raw/gone.md", null, null));
    }

    [Fact]
    public void Transitive_backrefs_follow_chains_through_a_cycle_and_leave_out_the_path_itself()
    {
        Assert.Equal(["wiki/a.md", "wiki/b.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", null, null));
        Assert.Equal(["wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "raw/gone.md", null, null));
    }

    [Fact]
    public void Transitive_backrefs_follow_only_links_of_the_given_kind()
    {
        Assert.Equal(["wiki/c.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/a.md", "body", null));
        Assert.Empty(LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", "body", null));
    }

    [Fact]
    public void Transitive_backrefs_pass_only_through_the_files_given()
    {
        // index → a → b ⇢ c: without index among the sources, the chain stops at a; without a, it stops at b.
        Assert.Equal(["wiki/a.md", "wiki/b.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", null, ["wiki/a.md", "wiki/b.md", "wiki/c.md"]));
        Assert.Equal(["wiki/b.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", null, ["wiki/b.md", "wiki/index.md"]));
    }

    [Fact]
    public void Sources_whose_paths_need_json_escaping_still_match()
    {
        const string source = "wiki/café \"x\" \\ y.md";
        AddFile(source, "markdown");
        Link(source, 1, "body", "path", "a.md", "wiki/a.md", "A");

        Assert.Equal([new LinkIn(source, 1, "body", "a.md", "A")], LinkQueries.Backrefs(_db, "wiki/a.md", null, [source]));
        Assert.Equal([source], LinkQueries.TransitiveBackrefs(_db, "wiki/a.md", null, [source]));
    }

    [Fact]
    public void Sources_lists_every_file_with_a_link_out()
    {
        Assert.Equal(["wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/index.md", "wiki/self.md"], LinkQueries.Sources(_db, null).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Broken_lists_every_path_link_whose_target_is_not_an_indexed_file_or_folder()
    {
        Assert.Equal(
            [new BrokenLink("wiki/a.md", 4, "body", "../raw/gone.md", "raw/gone.md"), new BrokenLink("wiki/a.md", 7, "body", "../../x.md", null)],
            LinkQueries.Broken(_db));
    }

    [Fact]
    public void A_link_is_no_longer_broken_once_its_target_is_indexed()
    {
        AddFile("raw/gone.md", "markdown");

        Assert.DoesNotContain(LinkQueries.Broken(_db), link => link.Target == "raw/gone.md");
        Assert.Equal("file", LinkQueries.Refs(_db, "wiki/a.md").Single(link => link.Line == 4).Type);
    }

    [Fact]
    public void A_path_link_to_a_folder_holding_an_indexed_file_is_a_directory()
    {
        AddFile("wiki/folders.md", "markdown");
        Link("wiki/folders.md", 1, "body", "path", "../", "", "root");
        Link("wiki/folders.md", 2, "body", "path", "../raw/", "raw", "raw");
        Link("wiki/folders.md", 3, "body", "path", "a", "wiki/a", "a");
        Link("wiki/folders.md", 4, "body", "path", "../ra", "ra", "ra");
        Link("wiki/folders.md", 5, "frontmatter", "path", "../wik", "wik", null);

        // wiki/a.md does not make wiki/a a folder, nor raw/x.md make ra or wik one: only a path under target/ counts.
        Assert.Equal(["directory", "directory", "missing", "missing", "missing"], LinkQueries.Refs(_db, "wiki/folders.md").Select(l => l.Type));
        Assert.Equal(["wiki/a", "ra", "wik"], LinkQueries.Broken(_db).Where(l => l.Source == "wiki/folders.md").Select(l => l.Target));
        Assert.Equal([new LinkIn("wiki/folders.md", 2, "body", "../raw/", "raw")], LinkQueries.Backrefs(_db, "raw", null, null));
    }
}
