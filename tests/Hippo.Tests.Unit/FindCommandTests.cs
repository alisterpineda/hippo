using Hippo.Commands;
using Hippo.Commands.Find;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>
/// Asks <see cref="FindCommand.Run"/> what <c>find</c> lists, with no command line and no output: the options go in
/// bound, and the records come back as they would be printed. The integration tests cover the binding, the validators
/// and the two forms of output.
/// </summary>
public sealed class FindCommandTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly TestDatabase _database = new();
    private readonly SqliteConnection _db;
    private readonly Workspace _workspace;

    public FindCommandTests()
    {
        _dir.Write(".hippo/config.json", "");
        _dir.Write("wiki/alpha.md", "---\ntype: Topic\nas_of: 2026-04-01\n---\n# Alpha\n\nThe quick brown fox.\n");
        _dir.Write("wiki/beta.md", "---\ntype: Person\n---\n# Beta\n\nA fox, and another fox, and a hound.\n");
        _dir.Write("notes/bad.md", "---\ntitle: [\n---\n");
        _dir.Write("raw/c.png", "png");
        _workspace = Workspace.Open(_dir.FullPath);
        _db = _database.Open();
        Sweeper.Run(_workspace, _db, rebuild: false, new TestClock(DateTimeOffset.UtcNow.AddHours(1)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
        _dir.Dispose();
    }

    private List<FindOutput> Run(FindOptions options, bool titles = false) => FindCommand.Run(options, _workspace, _db, titles).Files;

    [Fact]
    public void Without_a_query_every_file_is_listed_in_path_order()
    {
        Assert.Equal(["notes/bad.md", "raw/c.png", "wiki/alpha.md", "wiki/beta.md"], Run(new FindOptions()).Select(f => f.Path));
    }

    [Fact]
    public void Glob_kind_and_where_narrow_the_listing()
    {
        Assert.Equal(["wiki/alpha.md", "wiki/beta.md"], Run(new FindOptions { Globs = ["wiki/**"] }).Select(f => f.Path));
        Assert.Equal(["raw/c.png"], Run(new FindOptions { Kind = "other" }).Select(f => f.Path));
        Assert.Equal(["wiki/alpha.md"],
            Run(new FindOptions { Where = [FrontmatterFilter.Parse("as_of<2026-05-01")] }).Select(f => f.Path));
    }

    [Fact]
    public void Errors_only_keeps_the_files_whose_frontmatter_failed_to_parse()
    {
        var found = Assert.Single(Run(new FindOptions { ErrorsOnly = true }));
        Assert.Equal("notes/bad.md", found.Path);
        Assert.NotNull(found.ParseError);
    }

    [Fact]
    public void Fields_are_read_only_when_asked_for()
    {
        Assert.All(Run(new FindOptions()), f => Assert.Null(f.Fields));

        var fields = Run(new FindOptions { Globs = ["wiki/alpha.md"], Fields = ["type", "as_of"] }).Single().Fields!;
        Assert.Equal("Topic", fields["type"].GetString());
        Assert.Equal("2026-04-01", fields["as_of"].GetString());
    }

    [Fact]
    public void Titles_are_read_only_when_asked_for()
    {
        Assert.All(Run(new FindOptions { Kind = "markdown" }), f => Assert.Null(f.Title));
        Assert.Equal([null, "Alpha", "Beta"], Run(new FindOptions { Kind = "markdown" }, titles: true).Select(f => f.Title));
    }

    [Fact]
    public void A_query_lists_the_pages_holding_it_best_match_first_with_a_snippet()
    {
        var found = Run(new FindOptions { Query = "fox" });

        Assert.Equal(["wiki/beta.md", "wiki/alpha.md"], found.Select(f => f.Path));
        Assert.All(found, f => Assert.Contains("fox", f.Snippet!));
        Assert.Equal(["Beta", "Alpha"], found.Select(f => f.Title));
    }

    [Fact]
    public void A_limit_caps_either_form()
    {
        Assert.Single(Run(new FindOptions { Limit = 1 }));
        Assert.Single(Run(new FindOptions { Query = "fox", Limit = 1 }));
    }

    [Fact]
    public void A_query_with_no_word_in_it_is_an_error()
    {
        var error = Assert.Throws<HippoException>(() => Run(new FindOptions { Query = "\"\"" }));
        Assert.Contains("at least one word", error.Message);
    }
}
