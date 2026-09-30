using Dapper;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>Runs every query in <see cref="LinkQueries"/> against a database built from the migration scripts, over a
/// small graph with a cycle: index → a → b ⇢ c → a (⇢ is a frontmatter link).</summary>
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

        Link("wiki/a.md", 3, "body", "path", "b.md", "wiki/b.md");
        Link("wiki/a.md", 4, "body", "path", "../raw/gone.md", "raw/gone.md");
        Link("wiki/a.md", 5, "body", "url", "https://example.com", null);
        Link("wiki/a.md", 6, "body", "anchor", "#top", null);
        Link("wiki/a.md", 7, "body", "path", "../../x.md", null);
        Link("wiki/b.md", 2, "frontmatter", "path", "c.md", "wiki/c.md");
        Link("wiki/c.md", 1, "body", "path", "a.md", "wiki/a.md");
        Link("wiki/index.md", 1, "body", "path", "a.md", "wiki/a.md");
        Link("wiki/self.md", 1, "body", "path", "self.md", "wiki/self.md");
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    private void AddFile(string path, string kind) =>
        _db.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES (@path, 0, 1, 'h', 0, @kind)", new { path, kind });

    private void Link(string source, int line, string kind, string type, string raw, string? target) =>
        _db.Execute("""
            INSERT INTO links (source_id, line, kind, type, raw, target)
            SELECT id, @line, @kind, @type, @raw, @target FROM files WHERE path = @source
            """, new { source, line, kind, type, raw, target });

    [Fact]
    public void Refs_lists_a_files_links_in_line_order_with_their_class()
    {
        Assert.Equal(
            [
                new LinkOut(3, "body", "file", "b.md", "wiki/b.md"),
                new LinkOut(4, "body", "missing", "../raw/gone.md", "raw/gone.md"),
                new LinkOut(5, "body", "url", "https://example.com", null),
                new LinkOut(6, "body", "anchor", "#top", null),
                new LinkOut(7, "body", "missing", "../../x.md", null),
            ],
            LinkQueries.Refs(_db, "wiki/a.md"));
    }

    [Fact]
    public void Refs_of_a_file_without_links_is_empty()
    {
        Assert.Empty(LinkQueries.Refs(_db, "img.png"));
    }

    [Fact]
    public void Backrefs_lists_the_links_into_a_path()
    {
        Assert.Equal(
            [new LinkIn("wiki/c.md", 1, "body", "a.md"), new LinkIn("wiki/index.md", 1, "body", "a.md")],
            LinkQueries.Backrefs(_db, "wiki/a.md", null));
    }

    [Fact]
    public void Backrefs_can_be_limited_to_one_kind()
    {
        Assert.Equal([new LinkIn("wiki/b.md", 2, "frontmatter", "c.md")], LinkQueries.Backrefs(_db, "wiki/c.md", "frontmatter"));
        Assert.Empty(LinkQueries.Backrefs(_db, "wiki/c.md", "body"));
    }

    [Fact]
    public void Backrefs_of_a_missing_path_lists_the_links_that_break_on_it()
    {
        Assert.Equal([new LinkIn("wiki/a.md", 4, "body", "../raw/gone.md")], LinkQueries.Backrefs(_db, "raw/gone.md", null));
    }

    [Fact]
    public void Transitive_backrefs_follow_chains_through_a_cycle_and_leave_out_the_path_itself()
    {
        Assert.Equal(["wiki/a.md", "wiki/b.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", null));
        Assert.Equal(["wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "raw/gone.md", null));
    }

    [Fact]
    public void Transitive_backrefs_follow_only_links_of_the_given_kind()
    {
        Assert.Equal(["wiki/c.md", "wiki/index.md"], LinkQueries.TransitiveBackrefs(_db, "wiki/a.md", "body"));
        Assert.Empty(LinkQueries.TransitiveBackrefs(_db, "wiki/c.md", "body"));
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
        Link("wiki/folders.md", 1, "body", "path", "../", "");
        Link("wiki/folders.md", 2, "body", "path", "../raw/", "raw");
        Link("wiki/folders.md", 3, "body", "path", "a", "wiki/a");
        Link("wiki/folders.md", 4, "body", "path", "../ra", "ra");
        Link("wiki/folders.md", 5, "frontmatter", "path", "../wik", "wik");

        // wiki/a.md does not make wiki/a a folder, nor raw/x.md make ra or wik one: only a path under target/ counts.
        Assert.Equal(["directory", "directory", "missing", "missing", "missing"], LinkQueries.Refs(_db, "wiki/folders.md").Select(l => l.Type));
        Assert.Equal(["wiki/a", "ra", "wik"], LinkQueries.Broken(_db).Where(l => l.Source == "wiki/folders.md").Select(l => l.Target));
        Assert.Equal([new LinkIn("wiki/folders.md", 2, "body", "../raw/")], LinkQueries.Backrefs(_db, "raw", null));
        Assert.Contains("wiki/folders.md", LinkQueries.Orphans(_db));
    }

    [Fact]
    public void Orphans_are_pages_with_no_link_to_or_from_another_file()
    {
        AddFile("wiki/loose.md", "markdown");
        Link("wiki/loose.md", 1, "body", "url", "https://example.com", null);
        Link("wiki/loose.md", 2, "body", "anchor", "#top", null);
        Link("wiki/loose.md", 3, "body", "path", "gone.md", "wiki/gone.md");
        AddFile("wiki/gallery.md", "markdown");
        Link("wiki/gallery.md", 1, "body", "path", "../img.png", "img.png");

        // wiki/index.md is linked to by nothing, but links out; wiki/gallery.md links only to a file that is not a page.
        Assert.Equal(["raw/x.md", "wiki/loose.md", "wiki/self.md"], LinkQueries.Orphans(_db));
    }
}
