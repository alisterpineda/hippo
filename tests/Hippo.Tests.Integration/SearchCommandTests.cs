using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Integration;

public sealed class SearchCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    /// <summary>The paths <c>search --json</c> returns, in rank order.</summary>
    private List<string> Paths(params string[] args) =>
        Json(_workspace.Run(["search", .. args, "--json"])).EnumerateArray().Select(r => r.GetProperty("path").GetString()!).ToList();

    [Fact]
    public void Search_prints_each_matching_page_with_its_title_and_a_snippet()
    {
        _workspace.Write("wiki/heron.md", "---\ntitle: Herons\n---\nThe grey heron waits by the water.\n");
        _workspace.Write("wiki/owl.md", "# Owls\n\nOwls hunt at night.\n");

        var result = _workspace.Run("search", "heron");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal("wiki/heron.md  Herons\n  The grey **heron** waits by the water.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Search_json_gives_path_title_and_snippet()
    {
        _workspace.Write("a.md", "# Alpha\n\nA kestrel hovers.\n");

        var json = Json(_workspace.Run("search", "kestrel", "--json"));

        var result = Assert.Single(json.EnumerateArray());
        Assert.Equal("a.md", result.GetProperty("path").GetString());
        Assert.Equal("Alpha", result.GetProperty("title").GetString());
        Assert.Equal("# Alpha A **kestrel** hovers.", result.GetProperty("snippet").GetString());
    }

    [Fact]
    public void A_page_with_no_title_prints_its_path_alone()
    {
        _workspace.Write("a.md", "Plain kestrel text.\n");

        var result = _workspace.Run("search", "kestrel");

        Assert.Equal("a.md\n  Plain **kestrel** text.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_snippet_is_the_body_around_the_match_on_one_line()
    {
        string Words(string prefix) => string.Join(' ', Enumerable.Range(0, 60).Select(i => $"{prefix}{i}"));
        _workspace.Write("a.md", $"{Words("before")}\n\nThe kestrel\nhovers over the field.\n\n{Words("after")}\n");

        var snippet = Assert.Single(Json(_workspace.Run("search", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString()!;

        Assert.Contains("**kestrel** hovers", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.DoesNotContain("before0 ", snippet);
        Assert.DoesNotContain("after59", snippet);
    }

    [Fact]
    public void Nothing_found_is_not_an_error()
    {
        _workspace.Write("a.md", "# A\n");

        var text = _workspace.Run("search", "kestrel");
        var json = _workspace.Run("search", "kestrel", "--json");

        Assert.Equal((0, ""), (text.ExitCode, text.Stdout));
        Assert.Equal((0, "[]"), (json.ExitCode, json.Stdout.Trim()));
    }

    [Fact]
    public void The_default_tokenizer_matches_other_forms_of_a_word()
    {
        _workspace.Write("a.md", "She runs every morning.\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths("running"));
    }

    [Fact]
    public void The_trigram_tokenizer_matches_inside_words()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "Every page is in the index.\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths("dex"));
    }

    [Fact]
    public void Under_the_trigram_tokenizer_a_word_shorter_than_three_characters_finds_nothing()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "Every page is in the index.\n");
        _workspace.Write("b.md", "C# index notes.\n");

        Assert.Empty(Paths("C# index"));
        Assert.Empty(Paths("C#"));
    }

    [Fact]
    public void A_trigram_snippet_holds_whole_words_around_the_match()
    {
        string Words(string prefix) => string.Join(' ', Enumerable.Range(0, 60).Select(i => $"{prefix}{i}"));
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", $"{Words("before")}\n\nThe kestrel\nhovers over the field.\n\n{Words("after")}\n");

        var snippet = Assert.Single(Json(_workspace.Run("search", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString()!;

        Assert.Contains("The **kestrel** hovers over the field.", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);
        // Every word is whole: none is a cut-off piece of before58, after0 or the like.
        Assert.All(snippet[3..^3].Split(' '), word => Assert.Matches(@"^(before\d+|after\d+|The|\*\*kestrel\*\*|hovers|over|the|field\.)$", word));
    }

    [Fact]
    public void A_trigram_snippet_keeps_a_match_beside_where_it_is_cut()
    {
        string Words(string prefix) => string.Join(' ', Enumerable.Range(0, 60).Select(i => $"{prefix}{i}"));
        var between = string.Join(' ', Enumerable.Repeat("mid", 13));
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        // The 64-character window then starts at kestrel and ends at falcon, so each match sits beside a cut.
        _workspace.Write("a.md", $"{Words("before")} kestrel {between} falcon {Words("after")}\n");

        var snippet = Assert.Single(Json(_workspace.Run("search", "kestrel falcon", "--json")).EnumerateArray()).GetProperty("snippet").GetString();

        Assert.Equal($"...**kestrel** {between} **falcon**...", snippet);
    }

    [Fact]
    public void A_trigram_snippet_keeps_the_words_beside_dots_the_page_itself_holds()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "...and the kestrel waits...\n");

        var snippet = Assert.Single(Json(_workspace.Run("search", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString();

        Assert.Equal("...and the **kestrel** waits...", snippet);
    }

    [Fact]
    public void Every_word_of_the_query_must_appear()
    {
        _workspace.Write("both.md", "The heron and the kestrel.\n");
        _workspace.Write("heron.md", "Just a heron.\n");
        _workspace.Write("kestrel.md", "Just a kestrel.\n");

        Assert.Equal(["both.md"], Paths("kestrel heron"));
    }

    [Theory]
    [InlineData("foo-bar")]
    [InlineData("C#")]
    [InlineData("2.0")]
    [InlineData("\"unclosed")]
    [InlineData("title:foo")]
    [InlineData("a AND OR NOT")]
    [InlineData("star*")]
    public void Query_punctuation_is_matched_as_text_never_parsed_as_syntax(string query)
    {
        _workspace.Write("a.md", "foo-bar in C# 2.0, \"unclosed\", title:foo, a AND OR NOT, star*\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_is_an_error(string query)
    {
        var result = _workspace.Run("search", query);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("search", result.Stderr);
    }

    [Fact]
    public void A_title_match_ranks_above_a_body_match()
    {
        _workspace.Write("body.md", "A zebra, and another zebra.\n");
        _workspace.Write("title.md", "---\ntitle: Zebra\n---\nStriped animals.\n");

        Assert.Equal(["title.md", "body.md"], Paths("zebra"));
    }

    [Fact]
    public void A_path_match_ranks_above_a_body_match()
    {
        _workspace.Write("body.md", "A zebra, and another zebra.\n");
        _workspace.Write("zebra.md", "Striped animals.\n");

        Assert.Equal(["zebra.md", "body.md"], Paths("zebra"));
    }

    [Fact]
    public void A_title_match_ranks_above_a_path_match()
    {
        _workspace.Write("zebra.md", "Striped animals.\n");
        _workspace.Write("title.md", "---\ntitle: Zebra\n---\nStriped animals.\n");

        Assert.Equal(["title.md", "zebra.md"], Paths("zebra"));
    }

    [Fact]
    public void More_matches_rank_higher()
    {
        _workspace.Write("once.md", "One heron among many other birds and things.\n");
        _workspace.Write("often.md", "Heron after heron after heron.\n");

        Assert.Equal(["often.md", "once.md"], Paths("heron"));
    }

    [Fact]
    public void A_page_is_found_by_its_path()
    {
        _workspace.Write("projects/kestrel.md", "Nothing in the body says it.\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["projects/kestrel.md"], Paths("kestrel"));
    }

    [Fact]
    public void Files_that_are_not_markdown_are_not_searched()
    {
        _workspace.Write("notes.txt", "kestrel\n");

        Assert.Empty(Paths("kestrel"));
    }

    [Fact]
    public void Glob_keeps_only_matching_paths()
    {
        _workspace.Write("wiki/a.md", "kestrel\n");
        _workspace.Write("raw/b.md", "kestrel\n");

        Assert.Equal(["wiki/a.md"], Paths("kestrel", "--glob", "wiki/**"));
    }

    [Fact]
    public void Where_keeps_only_pages_whose_frontmatter_matches()
    {
        _workspace.Write("a.md", "---\ntype: Topic\n---\nkestrel\n");
        _workspace.Write("b.md", "---\ntype: Person\n---\nkestrel\n");
        _workspace.Write("c.md", "---\ntype: Topic\n---\nheron\n");

        Assert.Equal(["a.md"], Paths("kestrel", "--where", "type=Topic"));
    }

    [Fact]
    public void Glob_and_where_combine_with_the_query_and_each_other()
    {
        _workspace.Write("wiki/a.md", "---\ntype: Topic\n---\nkestrel\n");
        _workspace.Write("wiki/b.md", "---\ntype: Person\n---\nkestrel\n");
        _workspace.Write("raw/c.md", "---\ntype: Topic\n---\nkestrel\n");
        _workspace.Write("wiki/d.md", "---\ntype: Topic\n---\nheron\n");

        Assert.Equal(["wiki/a.md"], Paths("kestrel", "--glob", "wiki/**", "--where", "type=Topic"));
    }

    [Fact]
    public void A_malformed_where_is_an_error()
    {
        var result = _workspace.Run("search", "kestrel", "--where", "type");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Limit_keeps_the_best_ranked_results()
    {
        _workspace.Write("one.md", "heron and many other words here.\n");
        _workspace.Write("two.md", "heron heron and other words.\n");
        _workspace.Write("three.md", "heron heron heron.\n");

        Assert.Equal(["three.md", "two.md"], Paths("heron", "--limit", "2"));
    }

    [Fact]
    public void Limit_applies_after_the_filters()
    {
        _workspace.Write("raw/a.md", "heron heron heron\n");
        _workspace.Write("wiki/b.md", "heron and other words\n");

        Assert.Equal(["wiki/b.md"], Paths("heron", "--glob", "wiki/**", "--limit", "1"));
    }

    [Fact]
    public void Without_a_limit_there_are_at_most_20_results()
    {
        for (var i = 0; i < 25; i++)
        {
            _workspace.Write($"p{i:d2}.md", "heron\n");
        }

        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"p{i:d2}.md"), Paths("heron"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_limit_below_1_is_an_error(string limit)
    {
        var result = _workspace.Run("search", "heron", "--limit", limit);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--limit", result.Stderr);
    }

    [Fact]
    public void An_edit_is_searchable_on_the_next_command()
    {
        var path = _workspace.Write("a.md", "heron\n");
        Assert.Equal(["a.md"], Paths("heron"));

        File.WriteAllText(path, "kestrel\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(Paths("heron"));
        Assert.Equal(["a.md"], Paths("kestrel"));
    }

    [Fact]
    public void An_index_from_before_search_existed_has_every_page_searchable_after_migrating()
    {
        _workspace.Write("a.md", "heron\n");
        _workspace.Settle();
        var database = Json(_workspace.Run("status", "--json")).GetProperty("database").GetString()!;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // The schema as it was one version back: no search table, and nothing in the files table changed.
            command.CommandText = "DROP TABLE search; PRAGMA user_version = 3";
            command.ExecuteNonQuery();
        }

        Assert.Equal(["a.md"], Paths("heron"));
    }
}
