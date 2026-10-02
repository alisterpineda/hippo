using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Integration;

public sealed class FindCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    /// <summary>The paths <c>find --json</c> returns, in order.</summary>
    private List<string> Paths(params string[] args) =>
        Json(_workspace.Run(["find", .. args, "--json"])).EnumerateArray().Select(r => r.GetProperty("path").GetString()!).ToList();

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Without_a_query_find_lists_every_indexed_path()
    {
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("raw/b.txt", "b");

        var result = _workspace.Run("find");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["raw/b.txt", "wiki/a.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Without_a_query_glob_filters_by_workspace_relative_pattern()
    {
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("wiki/deep/b.md", "# B\n");
        _workspace.Write("raw/c.md", "# C\n");

        var result = _workspace.Run("find", "--glob", "wiki/**/*.md");

        Assert.Equal(["wiki/a.md", "wiki/deep/b.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Without_a_query_where_filters_by_frontmatter_value()
    {
        _workspace.Write("wiki/topic.md", "---\ntype: Topic\n---\n");
        _workspace.Write("wiki/person.md", "---\ntype: Person\n---\n");

        var result = _workspace.Run("find", "--where", "type=Topic");

        Assert.Equal(["wiki/topic.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Without_a_query_glob_and_where_combine()
    {
        _workspace.Write("wiki/topic.md", "---\ntype: Topic\n---\n");
        _workspace.Write("drafts/topic.md", "---\ntype: Topic\n---\n");
        _workspace.Write("wiki/person.md", "---\ntype: Person\n---\n");

        var result = _workspace.Run("find", "--glob", "wiki/**", "--where", "type=Topic");

        Assert.Equal(["wiki/topic.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Errors_lists_only_files_whose_frontmatter_failed_to_parse_with_the_error()
    {
        _workspace.Write("good.md", "---\ntitle: A\n---\n");
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");
        _workspace.Write("c.txt", "c");

        var result = _workspace.Run("find", "--errors");

        Assert.Equal(0, result.ExitCode);
        var line = Assert.Single(Lines(result.Stdout));
        Assert.StartsWith("bad.md: line ", line);
    }

    [Fact]
    public void Without_a_query_json_lists_path_kind_size_modified_title_parse_error_and_no_snippet()
    {
        _workspace.Write("a.md", "---\ntitle: Alpha\n---\n");

        var json = Json(_workspace.Run("find", "--glob", "*.md", "--json"));

        var file = Assert.Single(json.EnumerateArray());
        Assert.Equal("a.md", file.GetProperty("path").GetString());
        Assert.Equal("markdown", file.GetProperty("kind").GetString());
        Assert.Equal(21, file.GetProperty("size").GetInt64());
        Assert.Equal(File.GetLastWriteTimeUtc(_workspace.Combine("a.md")), file.GetProperty("modified").GetDateTimeOffset().UtcDateTime);
        Assert.Equal("Alpha", file.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("parseError").ValueKind);
        Assert.Equal(JsonValueKind.Null, file.GetProperty("snippet").ValueKind);
    }

    [Fact]
    public void Without_a_query_a_page_titled_by_its_heading_has_that_title()
    {
        _workspace.Write("a.md", "# Alpha\n\nText.\n");

        var file = Assert.Single(Json(_workspace.Run("find", "--json")).EnumerateArray());

        Assert.Equal("Alpha", file.GetProperty("title").GetString());
    }

    [Fact]
    public void Without_a_query_a_page_with_no_title_and_a_file_that_is_not_a_page_have_a_null_title()
    {
        _workspace.Write("a.md", "Just text.\n");
        _workspace.Write("b.txt", "# Not a page\n");

        var json = Json(_workspace.Run("find", "--json"));

        Assert.Equal([JsonValueKind.Null, JsonValueKind.Null], json.EnumerateArray().Select(f => f.GetProperty("title").ValueKind));
    }

    [Fact]
    public void Json_rows_carry_the_parse_error()
    {
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var json = Json(_workspace.Run("find", "--json"));

        var file = Assert.Single(json.EnumerateArray());
        Assert.StartsWith("line ", file.GetProperty("parseError").GetString());
    }

    [Fact]
    public void Without_a_query_there_is_no_limit_unless_one_is_given()
    {
        for (var i = 0; i < 25; i++)
        {
            _workspace.Write($"p{i:d2}.md", "heron\n");
        }

        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"p{i:d2}.md"), Paths());
        Assert.Equal(["p00.md", "p01.md", "p02.md"], Paths("--limit", "3"));
    }

    [Fact]
    public void Without_a_query_limit_applies_after_the_filters()
    {
        _workspace.Write("raw/a.md", "---\ntitle: [unclosed\n---\n");
        _workspace.Write("wiki/b.md", "# B\n");
        _workspace.Write("wiki/c.md", "---\ntitle: [unclosed\n---\n");

        Assert.Equal(["wiki/b.md"], Paths("--glob", "wiki/**", "--limit", "1"));
        Assert.Equal(["raw/a.md"], Paths("--errors", "--limit", "1"));
        Assert.Equal(["wiki/c.md"], Paths("--glob", "wiki/**", "--errors", "--limit", "1"));
    }

    [Fact]
    public void Without_a_query_finding_nothing_is_not_an_error()
    {
        _workspace.Write("a.md", "# A\n");

        var text = _workspace.Run("find", "--glob", "*.txt");
        var json = _workspace.Run("find", "--glob", "*.txt", "--json");

        Assert.Equal((0, ""), (text.ExitCode, text.Stdout));
        Assert.Equal((0, "[]"), (json.ExitCode, json.Stdout.Trim()));
    }

    [Fact]
    public void With_a_query_errors_keeps_only_matching_pages_whose_frontmatter_failed_to_parse()
    {
        _workspace.Write("good.md", "---\ntitle: A\n---\nkestrel\n");
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\nkestrel\n");
        _workspace.Write("other.md", "---\ntitle: [unclosed\n---\nheron\n");

        var file = Assert.Single(Json(_workspace.Run("find", "kestrel", "--errors", "--json")).EnumerateArray());

        Assert.Equal("bad.md", file.GetProperty("path").GetString());
        Assert.StartsWith("line ", file.GetProperty("parseError").GetString());
    }

    [Fact]
    public void With_a_query_errors_prints_each_page_as_a_query_does_without_it()
    {
        _workspace.Write("bad.md", "---\ntags: [unclosed\n---\n# Herons\n\nA kestrel.\n");

        var result = _workspace.Run("find", "kestrel", "--errors");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("bad.md  Herons\n  # Herons A **kestrel**.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_query_prints_each_matching_page_with_its_title_and_a_snippet()
    {
        _workspace.Write("wiki/heron.md", "---\ntitle: Herons\n---\nThe grey heron waits by the water.\n");
        _workspace.Write("wiki/owl.md", "# Owls\n\nOwls hunt at night.\n");

        var result = _workspace.Run("find", "heron");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal("wiki/heron.md  Herons\n  The grey **heron** waits by the water.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_query_json_gives_the_file_its_title_and_a_snippet()
    {
        _workspace.Write("a.md", "# Alpha\n\nA kestrel hovers.\n");

        var json = Json(_workspace.Run("find", "kestrel", "--json"));

        var result = Assert.Single(json.EnumerateArray());
        Assert.Equal("a.md", result.GetProperty("path").GetString());
        Assert.Equal("markdown", result.GetProperty("kind").GetString());
        Assert.Equal(27, result.GetProperty("size").GetInt64());
        Assert.Equal(File.GetLastWriteTimeUtc(_workspace.Combine("a.md")), result.GetProperty("modified").GetDateTimeOffset().UtcDateTime);
        Assert.Equal("Alpha", result.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("parseError").ValueKind);
        Assert.Equal("# Alpha A **kestrel** hovers.", result.GetProperty("snippet").GetString());
    }

    [Fact]
    public void A_page_with_no_title_prints_its_path_alone()
    {
        _workspace.Write("a.md", "Plain kestrel text.\n");

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal("a.md\n  Plain **kestrel** text.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_snippet_is_the_body_around_the_match_on_one_line()
    {
        string Words(string prefix) => string.Join(' ', Enumerable.Range(0, 60).Select(i => $"{prefix}{i}"));
        _workspace.Write("a.md", $"{Words("before")}\n\nThe kestrel\nhovers over the field.\n\n{Words("after")}\n");

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString()!;

        Assert.Contains("**kestrel** hovers", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.DoesNotContain("before0 ", snippet);
        Assert.DoesNotContain("after59", snippet);
    }

    [Fact]
    public void A_query_finding_nothing_is_not_an_error()
    {
        _workspace.Write("a.md", "# A\n");

        var text = _workspace.Run("find", "kestrel");
        var json = _workspace.Run("find", "kestrel", "--json");

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

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString()!;

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

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel falcon", "--json")).EnumerateArray()).GetProperty("snippet").GetString();

        Assert.Equal($"...**kestrel** {between} **falcon**...", snippet);
    }

    [Fact]
    public void A_trigram_snippet_keeps_the_words_beside_dots_the_page_itself_holds()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "...and the kestrel waits...\n");

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString();

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
        var result = _workspace.Run("find", query);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("find needs at least one word to look for", result.Stderr);
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
        var result = _workspace.Run("find", "kestrel", "--where", "type");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Without_a_query_a_malformed_where_is_an_error()
    {
        var result = _workspace.Run("find", "--where", "type");

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
    public void With_a_query_and_no_limit_there_are_at_most_20_results()
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
        var result = _workspace.Run("find", "heron", "--limit", limit);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--limit", result.Stderr);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Without_a_query_a_limit_below_1_is_an_error(string limit)
    {
        var result = _workspace.Run("find", "--limit", limit);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--limit", result.Stderr);
    }

    [Fact]
    public void Glob_can_be_given_more_than_once_and_keeps_what_any_pattern_matches()
    {
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("raw/b.md", "# B\n");
        _workspace.Write("drafts/c.md", "# C\n");

        Assert.Equal(["raw/b.md", "wiki/a.md"], Paths("--glob", "wiki/**", "--glob", "raw/**"));
    }

    [Fact]
    public void A_glob_starting_with_an_exclamation_mark_leaves_out_what_it_matches()
    {
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("wiki/drafts/b.md", "# B\n");
        _workspace.Write("raw/c.md", "# C\n");

        Assert.Equal(["wiki/a.md"], Paths("--glob", "wiki/**", "--glob", "!wiki/drafts/**"));
    }

    [Fact]
    public void Globs_that_all_exclude_leave_out_what_they_match_from_every_file()
    {
        _workspace.Write("wiki/a.md", "# A\n");
        _workspace.Write("archive/b.md", "# B\n");
        _workspace.Write("c.png", "png");

        Assert.Equal(["wiki/a.md"], Paths("--glob", "!archive/**", "--glob", "!*.png"));
    }

    [Fact]
    public void With_a_query_a_glob_starting_with_an_exclamation_mark_leaves_out_what_it_matches()
    {
        _workspace.Write("wiki/a.md", "kestrel\n");
        _workspace.Write("archive/b.md", "kestrel\n");

        Assert.Equal(["wiki/a.md"], Paths("kestrel", "--glob", "!archive/**"));
    }

    [Fact]
    public void Kind_keeps_only_files_of_that_kind()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.png", "png");
        _workspace.Write("c.txt", "c");

        Assert.Equal(["a.md"], Paths("--kind", "markdown"));
        Assert.Equal(["b.png", "c.txt"], Paths("--kind", "other"));
    }

    [Fact]
    public void With_a_query_kind_other_finds_nothing_since_only_markdown_is_searched()
    {
        _workspace.Write("a.md", "kestrel\n");

        Assert.Equal(["a.md"], Paths("kestrel", "--kind", "markdown"));
        Assert.Empty(Paths("kestrel", "--kind", "other"));
    }

    [Fact]
    public void A_kind_other_than_markdown_or_other_is_an_error()
    {
        var result = _workspace.Run("find", "--kind", "page");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("page", result.Stderr);
    }

    /// <summary>A small graph with a cycle, index → a → b ⇢ c → a (⇢ is a frontmatter link), and pages around it that
    /// link only to themselves, to a file that is not a page, or to nothing indexed.</summary>
    private void WriteGraph()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "related" }] } }""");
        _workspace.Write("wiki/a.md", "[b](b.md)\n[gone](../raw/gone.md)\n[web](https://example.com)\n[top](#top)\n[out](../../x.md)\n");
        _workspace.Write("wiki/b.md", "---\nrelated: c.md\n---\n# B\n");
        _workspace.Write("wiki/c.md", "[a](a.md)\n");
        _workspace.Write("wiki/index.md", "[a](a.md)\n");
        _workspace.Write("wiki/self.md", "[self](self.md)\n");
        _workspace.Write("wiki/loose.md", "[web](https://example.com) [top](#top) [gone](gone.md)\n");
        _workspace.Write("wiki/gallery.md", "![img](../img.png)\n");
        _workspace.Write("raw/x.md", "# X\n");
        _workspace.Write("img.png", "png");
    }

    [Fact]
    public void No_backrefs_keeps_files_no_other_file_links_to()
    {
        WriteGraph();

        // A page's link to itself does not count, so wiki/self.md is kept.
        Assert.Equal(["raw/x.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"], Paths("--no-backrefs"));
    }

    [Fact]
    public void No_refs_keeps_files_with_no_link_to_another_indexed_file()
    {
        WriteGraph();

        // Links to the page itself, URLs, anchors, missing files and paths outside the workspace do not count.
        Assert.Equal(["img.png", "raw/x.md", "wiki/loose.md", "wiki/self.md"], Paths("--no-refs"));
    }

    [Fact]
    public void No_refs_and_no_backrefs_together_keep_files_with_no_link_in_or_out()
    {
        WriteGraph();

        // wiki/index.md links out though nothing links to it, and wiki/gallery.md links only to img.png, a file that is
        // not a page; that one link keeps both from the list.
        Assert.Equal(["raw/x.md", "wiki/loose.md", "wiki/self.md"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void With_a_query_no_refs_and_no_backrefs_each_keep_only_the_matching_pages_they_match()
    {
        _workspace.Write("index.md", "kestrel [a](a.md)\n");
        _workspace.Write("a.md", "kestrel\n");
        _workspace.Write("loose.md", "kestrel\n");

        Assert.Equal(["a.md", "loose.md"], Paths("kestrel", "--no-refs").Order(StringComparer.Ordinal));
        Assert.Equal(["index.md", "loose.md"], Paths("kestrel", "--no-backrefs").Order(StringComparer.Ordinal));
        Assert.Equal(["loose.md"], Paths("kestrel", "--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void No_refs_and_no_backrefs_list_files_with_no_link_in_or_out_and_exit_0()
    {
        _workspace.Write(".hippo/config.json", """
            {
              "bundles": ["wiki"],
              "links": {
                "frontmatter": [{ "field": "sources[].resource", "resolve": "bundle" }]
              }
            }
            """);
        _workspace.Write("wiki/index.md", "# Index\n\n- [Topic](topics/topic.md)\n");
        _workspace.Write("wiki/topics/topic.md", """
            ---
            title: Topic
            sources:
              - id: j-2026-09-01
                resource: ../raw/journal/2026-09-01.md
              - id: j-gone
                resource: ../raw/journal/gone.md
            ---
            # Topic

            See [the index](/index.md), [[Wikilink]] and [the web](https://example.com).
            """);
        _workspace.Write("raw/journal/2026-09-01.md", "# Day\n\n![photo](img/photo%201.png)\n");
        _workspace.Write("raw/journal/img/photo 1.png", "png");
        _workspace.Write("raw/journal/lonely.md", "# Nobody cites me\n");

        var result = _workspace.Run("find", "--no-refs", "--no-backrefs");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["raw/journal/lonely.md"], Lines(result.Stdout));
        Assert.Equal(["raw/journal/lonely.md"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void No_refs_and_no_backrefs_list_nothing_in_a_workspace_where_every_file_is_linked()
    {
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");

        var result = _workspace.Run("find", "--no-refs", "--no-backrefs");

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
    }

    [Fact]
    public void A_page_that_only_links_out_has_refs_but_one_linking_only_outside_the_index_does_not()
    {
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("web.md", "[web](https://example.com) and [gone](gone.md)\n");

        Assert.Equal(["web.md"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void A_file_that_is_not_a_page_has_no_backrefs_when_nothing_links_to_it()
    {
        _workspace.Write("index.md", "![used](used.png)\n");
        _workspace.Write("used.png", "png");
        _workspace.Write("unused.png", "png");

        Assert.Equal(["unused.png"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void A_page_linking_only_to_a_folder_has_no_refs()
    {
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");
        _workspace.Write("folders.md", "[here](./)\n");

        Assert.Equal(["folders.md"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void A_page_linking_only_into_the_roots_hippo_folder_has_no_refs()
    {
        _workspace.Write("index.md", "[config](.hippo/config.json)\n");

        Assert.Equal(["index.md"], Paths("--no-refs", "--no-backrefs"));
    }

    [Fact]
    public void An_exclusion_glob_hides_each_pattern_given_from_files_with_no_links()
    {
        _workspace.Write("README.md", "# Readme\n");
        _workspace.Write("archive/2020/old.md", "# Old\n");
        _workspace.Write("loose.md", "# Loose\n");

        var one = _workspace.Run("find", "--no-refs", "--no-backrefs", "--glob", "!archive/**");
        var both = Paths("--no-refs", "--no-backrefs", "--glob", "!archive/**", "--glob", "!*.md");

        Assert.Equal(0, one.ExitCode);
        Assert.Equal(["README.md", "loose.md"], Lines(one.Stdout));
        Assert.Empty(both);
    }

    [Fact]
    public void An_exclusion_glob_is_workspace_relative_from_a_subfolder()
    {
        _workspace.Write("archive/old.md", "# Old\n");
        _workspace.Write("notes/loose.md", "# Loose\n");

        var result = _workspace.RunIn(_workspace.Combine("notes"), "find", "--no-refs", "--no-backrefs", "--glob", "!archive/**");

        Assert.Equal(["notes/loose.md"], Lines(result.Stdout));
    }

    [Fact]
    public void A_file_the_glob_leaves_out_still_has_its_links_count()
    {
        _workspace.Write("archive/old.md", "[note](../note.md)\n");
        _workspace.Write("note.md", "# Note\n");

        var result = _workspace.Run("find", "--no-refs", "--no-backrefs", "--glob", "!archive/**");

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
    }

    [Fact]
    public void A_link_to_a_file_the_glob_leaves_out_still_counts_as_a_ref()
    {
        _workspace.Write("note.md", "[old](archive/old.md)\n");
        _workspace.Write("archive/old.md", "# Old\n");

        var result = _workspace.Run("find", "--no-refs", "--glob", "!archive/**");

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
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
