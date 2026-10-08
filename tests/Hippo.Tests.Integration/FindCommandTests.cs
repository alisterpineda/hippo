using System.Text.Json;

namespace Hippo.Tests.Integration;

public sealed class FindCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>The files <c>find --json</c> lists, under <c>files</c>.</summary>
    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement.GetProperty("files");
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
        Assert.Equal((0, "{\n  \"files\": [],\n  \"truncated\": false\n}"), (json.ExitCode, json.Stdout.Trim().ReplaceLineEndings("\n")));
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
        Assert.Equal("bad.md  Herons\n  # Herons A kestrel.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_query_prints_each_matching_page_with_its_title_and_a_snippet()
    {
        _workspace.Write("wiki/heron.md", "---\ntitle: Herons\n---\nThe grey heron waits by the water.\n");
        _workspace.Write("wiki/owl.md", "# Owls\n\nOwls hunt at night.\n");

        var result = _workspace.Run("find", "heron");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal("wiki/heron.md  Herons\n  The grey heron waits by the water.\n", result.Stdout.ReplaceLineEndings("\n"));
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
        Assert.Equal("# Alpha A kestrel hovers.", result.GetProperty("snippet").GetString());
    }

    [Fact]
    public void A_page_with_no_title_prints_its_path_alone()
    {
        _workspace.Write("a.md", "Plain kestrel text.\n");

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal("a.md\n  Plain kestrel text.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Piped_text_prints_the_pages_own_markdown_with_no_markers()
    {
        _workspace.Write("a.md", "The **Market Approach** is ours.\n");

        var result = _workspace.Run("find", "Market Approach");

        Assert.Equal("a.md\n  The **Market Approach** is ours.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_json_snippet_holds_no_highlighting()
    {
        _workspace.Write("a.md", "The **Market Approach** is ours.\n");
        _workspace.Terminal = true;

        var json = Json(_workspace.Run("find", "Market Approach", "--json"));

        Assert.Equal("The **Market Approach** is ours.", Assert.Single(json.EnumerateArray()).GetProperty("snippet").GetString());
    }

    [Fact]
    public void On_a_terminal_each_match_in_the_snippet_is_bold()
    {
        _workspace.Write("a.md", "---\ntitle: Approach\n---\nThe **Market Approach** is ours.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "Market Approach");

        Assert.Equal("a.md  Approach\n  The **\e[1mMarket\e[22m \e[1mApproach\e[22m** is ours.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void On_a_terminal_the_pages_own_control_characters_are_still_escaped()
    {
        _workspace.Write("a.md", "A \u001b[31m kestrel\u0007 hovers.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal("a.md\n  A \\x1b[31m \e[1mkestrel\e[22m\\x07 hovers.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_page_cannot_mark_its_own_text_as_a_match()
    {
        _workspace.Write("a.md", "A kestrel \u0002hovers\u0003 \u0003 here\u0001.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a.md\n  A \e[1mkestrel\e[22m �hovers� � here�.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("", true)]
    public void No_color_turns_the_bold_off_unless_it_is_empty(string noColor, bool bold)
    {
        _workspace.Write("a.md", "A kestrel hovers.\n");
        _workspace.Terminal = true;

        var result = _workspace.RunWith(
            new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = _workspace.CacheDir, ["NO_COLOR"] = noColor }, "find", "kestrel");

        Assert.Equal(bold ? "a.md\n  A \e[1mkestrel\e[22m hovers.\n" : "a.md\n  A kestrel hovers.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Text_only_in_a_links_destination_is_found()
    {
        _workspace.Write("a.md", "See [the notes](kestrel-notes.md).\n");
        _workspace.Write("b.md", "Other notes.\n");

        Assert.Equal(["a.md"], Paths("kestrel"));
    }

    [Fact]
    public void The_snippet_is_the_body_around_the_match_on_one_line()
    {
        string Words(string prefix) => string.Join(' ', Enumerable.Range(0, 60).Select(i => $"{prefix}{i}"));
        _workspace.Write("a.md", $"{Words("before")}\n\nThe kestrel\nhovers over the field.\n\n{Words("after")}\n");

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString()!;

        Assert.Contains("The kestrel hovers", snippet);
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
        Assert.Equal((0, "{\n  \"files\": [],\n  \"truncated\": false\n}"), (json.ExitCode, json.Stdout.Trim().ReplaceLineEndings("\n")));
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

        Assert.Contains("The kestrel hovers over the field.", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);
        // Every word is whole: none is a cut-off piece of before58, after0 or the like.
        Assert.All(snippet[3..^3].Split(' '), word => Assert.Matches(@"^(before\d+|after\d+|The|kestrel|hovers|over|the|field\.)$", word));
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

        Assert.Equal($"...kestrel {between} falcon...", snippet);
    }

    [Fact]
    public void A_trigram_snippet_keeps_a_phrase_match_that_ends_beside_where_it_is_cut()
    {
        var between = new string('x', 41);
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        // The 64-character window then ends just after the phrase, so the cut is beside the phrase's last word.
        _workspace.Write("a.md", $"kestrel {between} integration test and then the rest of the page.\n");
        _workspace.Terminal = true;

        var snippet = Assert.Single(Json(_workspace.Run("find", "\"kestrel\" \"integration test\"", "--json")).EnumerateArray())
            .GetProperty("snippet").GetString();
        var result = _workspace.Run("find", "\"kestrel\" \"integration test\"");

        Assert.Equal($"kestrel {between} integration test...", snippet);
        Assert.Equal($"a.md\n  \e[1mkestrel\e[22m {between} \e[1mintegration test\e[22m...\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_trigram_snippet_keeps_the_words_beside_dots_the_page_itself_holds()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "...and the kestrel waits...\n");

        var snippet = Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString();

        Assert.Equal("...and the kestrel waits...", snippet);
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
    [InlineData("title:foo")]
    [InlineData("a AND OR NOT")]
    [InlineData("star*")]
    [InlineData("-minus NEAR")]
    [InlineData("\"title:foo, a AND OR NOT, star*\"")]
    [InlineData("\"-minus NEAR(x)\"")]
    public void Query_punctuation_is_matched_as_text_never_parsed_as_syntax(string query)
    {
        _workspace.Write("a.md", "foo-bar in C# 2.0, title:foo, a AND OR NOT, star* -minus NEAR(x)\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths(query));
    }

    [Fact]
    public void A_quoted_phrase_needs_its_words_side_by_side_and_in_order()
    {
        _workspace.Write("phrase.md", "Each integration test runs alone.\n");
        _workspace.Write("reversed.md", "A test integration, backwards.\n");
        _workspace.Write("apart.md", "Integration takes time; test it.\n");

        Assert.Equal(["phrase.md"], Paths("\"integration test\""));
        Assert.Equal(["reversed.md"], Paths("\"test integration\""));
    }

    [Fact]
    public void Phrases_and_words_must_all_appear()
    {
        _workspace.Write("both.md", "The integration test checks ranking.\n");
        _workspace.Write("phrase.md", "The integration test checks nothing else.\n");
        _workspace.Write("word.md", "Ranking of a test for integration.\n");

        Assert.Equal(["both.md"], Paths("\"integration test\" ranking"));
        Assert.Equal(["both.md"], Paths("ranking \"integration test\""));
    }

    [Fact]
    public void A_quote_next_to_a_word_still_starts_or_ends_a_phrase()
    {
        _workspace.Write("a.md", "kestrel, then integration test, then heron\n");
        _workspace.Write("b.md", "kestrel heron test integration\n");

        Assert.Equal(["a.md"], Paths("kestrel\"integration test\"heron"));
    }

    [Theory]
    [InlineData("\"integration test")]
    [InlineData("\"integration\" test\"")]
    [InlineData("\"")]
    public void An_unclosed_quote_is_an_error(string query)
    {
        var result = _workspace.Run("find", query);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unclosed quote in query", result.Stderr);
    }

    [Fact]
    public void An_empty_phrase_is_ignored()
    {
        _workspace.Write("a.md", "Just a kestrel.\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths("\"\" kestrel"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"\" \"  \"")]
    public void A_query_of_empty_phrases_is_an_error(string query)
    {
        var result = _workspace.Run("find", query);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("find needs at least one word to look for", result.Stderr);
    }

    [Fact]
    public void Under_the_default_tokenizer_a_phrase_matches_other_forms_of_its_words_and_ignores_punctuation()
    {
        _workspace.Write("a.md", "Left as per humans, unfilled.\n");
        _workspace.Write("b.md", "Nothing to see.\n");

        Assert.Equal(["a.md"], Paths("\"(per human, unfiled)\""));
    }

    [Fact]
    public void Under_the_trigram_tokenizer_a_phrase_matches_as_written_ignoring_case()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("marker.md", "A marker (Per Human, Unfiled) here.\n");
        _workspace.Write("forms.md", "Left as per humans, unfilled.\n");
        _workspace.Write("bare.md", "Left per human, unfiled.\n");

        Assert.Equal(["marker.md"], Paths("\"(per human, unfiled)\""));
    }

    [Fact]
    public void Under_the_trigram_tokenizer_a_phrase_of_three_characters_or_more_can_match_though_its_words_are_shorter()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "This is a test.\n");
        _workspace.Write("b.md", "Is it a test?\n");

        Assert.Equal(["a.md"], Paths("\"is a\" test"));
        Assert.Empty(Paths("\"is\" test"));
        Assert.Empty(Paths("is a test"));
    }

    [Fact]
    public void Under_the_trigram_tokenizer_a_phrase_matches_across_a_line_break_in_the_page()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "A marker (per\nhuman, unfiled) wrapped.\n");

        Assert.Equal(["a.md"], Paths("\"(per human, unfiled)\""));
    }

    [Fact]
    public void Under_the_trigram_tokenizer_whitespace_in_a_phrase_matches_one_space()
    {
        _workspace.Write(".hippo/config.json", """{ "search": { "tokenizer": "trigram" } }""");
        _workspace.Write("a.md", "A marker (per human, unfiled) here.\n");

        Assert.Equal(["a.md"], Paths("\"(per  human,\tunfiled)\""));
    }

    [Fact]
    public void A_phrase_matches_within_the_title_path_or_body_never_across_them()
    {
        _workspace.Write("a.md", "---\ntitle: Integration\n---\nTest notes.\n");
        _workspace.Write("b.md", "---\ntitle: Integration test\n---\nNotes.\n");

        Assert.Equal(["b.md"], Paths("\"integration test\""));
    }

    [Fact]
    public void A_phrase_snippet_marks_the_words_of_the_phrase()
    {
        _workspace.Write("a.md", "Each integration test\nruns alone.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "\"integration test\"");

        Assert.Equal("a.md\n  Each \e[1mintegration test\e[22m runs alone.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Options_may_come_before_or_after_a_query_with_a_phrase()
    {
        _workspace.Write("wiki/a.md", "An integration test.\n");
        _workspace.Write("raw/b.md", "An integration test.\n");

        Assert.Equal(["wiki/a.md"], Paths("--glob", "wiki/**", "\"integration test\""));
        Assert.Equal(["wiki/a.md"], Paths("\"integration test\"", "--glob", "wiki/**"));
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
    public void A_word_only_in_the_description_finds_the_page()
    {
        _workspace.Write("a.md", "---\ntitle: Birds\ndescription: Notes on the kestrel.\n---\nStriped animals.\n");
        _workspace.Write("b.md", "---\ntitle: Birds\n---\nStriped animals.\n");

        Assert.Equal(["a.md"], Paths("kestrel"));
    }

    [Fact]
    public void A_match_only_in_the_description_shows_the_description_as_its_snippet()
    {
        _workspace.Write("a.md", "---\ntitle: Birds\ndescription: Notes on the kestrel.\n---\nStriped animals.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal("a.md  Birds\n  Notes on the \e[1mkestrel\e[22m.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_match_in_the_body_and_the_description_shows_the_body()
    {
        _workspace.Write("a.md", "---\ndescription: Notes on the kestrel.\n---\nThe kestrel hovers.\n");

        Assert.Equal("The kestrel hovers.", Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString());
    }

    [Fact]
    public void A_match_only_in_the_title_shows_the_start_of_the_body()
    {
        _workspace.Write("a.md", "---\ntitle: Kestrel\ndescription: Notes on birds.\n---\nStriped animals.\n");

        Assert.Equal("Striped animals.", Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).GetProperty("snippet").GetString());
    }

    [Fact]
    public void A_description_cannot_mark_its_own_text_as_a_match()
    {
        _workspace.Write("a.md", "---\ndescription: \"A kestrel \\u0002hovers\\u0003 \\u0003 here\\u0001.\"\n---\nStriped animals.\n");
        _workspace.Terminal = true;

        var result = _workspace.Run("find", "kestrel");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a.md\n  A \e[1mkestrel\e[22m �hovers� � here�.\n", result.Stdout.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_description_that_is_not_text_is_not_searched()
    {
        _workspace.Write("a.md", "---\ndescription: [kestrel]\n---\nStriped animals.\n");

        Assert.Empty(Paths("kestrel"));
    }

    [Fact]
    public void A_description_match_ranks_between_a_title_match_and_a_path_match()
    {
        _workspace.Write("zebra.md", "Striped animals.\n");
        _workspace.Write("described.md", "---\ndescription: Zebra\n---\nStriped animals.\n");
        _workspace.Write("title.md", "---\ntitle: Zebra\n---\nStriped animals.\n");

        Assert.Equal(["title.md", "described.md", "zebra.md"], Paths("zebra"));
    }

    [Fact]
    public void A_phrase_does_not_match_across_the_description_and_the_body()
    {
        _workspace.Write("a.md", "---\ndescription: Integration\n---\nTest notes.\n");

        Assert.Empty(Paths("\"integration test\""));
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
        var result = _workspace.Run("find", "kestrel", "--where", "=Topic");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Without_a_query_a_malformed_where_is_an_error()
    {
        var result = _workspace.Run("find", "--where", "=Topic");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Every_where_must_hold()
    {
        _workspace.Write("wiki/old.md", "---\ntracking: open\nas_of: 2026-01-15\n---\n");
        _workspace.Write("wiki/new.md", "---\ntracking: open\nas_of: 2026-06-01\n---\n");
        _workspace.Write("wiki/closed.md", "---\ntracking: closed\nas_of: 2026-01-15\n---\n");

        Assert.Equal(["wiki/old.md"], Paths("--glob", "wiki/**", "--where", "tracking=open", "--where", "as_of<2026-04-01"));
    }

    [Fact]
    public void Where_present_and_missing_find_tracked_pages_with_no_date()
    {
        _workspace.Write("wiki/dated.md", "---\ntracking: open\nas_of: 2026-01-15\n---\n");
        _workspace.Write("wiki/undated.md", "---\ntracking: open\n---\n");
        _workspace.Write("wiki/blank.md", "---\ntracking: open\nas_of:\n---\n");
        _workspace.Write("wiki/untracked.md", "---\ntitle: U\n---\n");

        Assert.Equal(["wiki/blank.md", "wiki/undated.md"], Paths("--glob", "wiki/**", "--where", "tracking", "--where", "!as_of"));
    }

    [Fact]
    public void Where_not_equal_keeps_pages_without_the_field_but_not_those_whose_frontmatter_failed_to_parse()
    {
        _workspace.Write("draft.md", "---\ntags: [draft, x]\n---\n");
        _workspace.Write("final.md", "---\ntags: [x]\n---\n");
        _workspace.Write("untagged.md", "---\ntitle: U\n---\n");
        _workspace.Write("bad.md", "---\ntags: [unclosed\n---\n");

        Assert.Equal(["final.md", "untagged.md"], Paths("--kind", "markdown", "--where", "tags!=draft"));
    }

    [Fact]
    public void Where_compares_numbers_as_numbers()
    {
        _workspace.Write("two.md", "---\nrank: 2\n---\n");
        _workspace.Write("ten.md", "---\nrank: 10\n---\n");

        Assert.Equal(["ten.md"], Paths("--where", "rank>5"));
    }

    [Fact]
    public void With_a_query_every_where_must_hold()
    {
        _workspace.Write("a.md", "---\ntype: Topic\nrank: 3\n---\nkestrel\n");
        _workspace.Write("b.md", "---\ntype: Topic\nrank: 1\n---\nkestrel\n");
        _workspace.Write("c.md", "---\ntype: Topic\nrank: 3\n---\nheron\n");

        Assert.Equal(["a.md"], Paths("kestrel", "--where", "type=Topic", "--where", "rank>=2"));
    }

    [Theory]
    [InlineData("!sources")]
    [InlineData("tracking!=open")]
    [InlineData("!sources[].id")]
    [InlineData("a!=@b")]
    public void Where_matches_only_markdown_files(string condition)
    {
        _workspace.Write("pic.png", "png");
        _workspace.Write("notes.txt", "text");
        _workspace.Write("plain.md", "# Plain\n");

        Assert.Equal(["plain.md"], Paths("--where", condition));
    }

    [Fact]
    public void Where_through_brackets_finds_exactly_the_pages_citing_a_source()
    {
        _workspace.Write("wiki/cites.md", "---\nsources:\n  - id: j-2026-09-16\n    resource: raw/a.md\n  - id: j-2026-09-17\n---\n");
        _workspace.Write("wiki/other.md", "---\nsources:\n  - id: j-2026-09-17\n---\n");
        _workspace.Write("wiki/flat.md", "---\nsources: j-2026-09-16\nid: j-2026-09-16\n---\n");
        _workspace.Write("wiki/none.md", "# None\n");

        Assert.Equal(["wiki/cites.md"], Paths("--where", "sources[].id=j-2026-09-16"));
    }

    [Fact]
    public void Where_without_brackets_does_not_step_into_a_list()
    {
        _workspace.Write("cites.md", "---\nsources:\n  - id: x\n---\n");

        Assert.Empty(Paths("--where", "sources.id=x"));
        Assert.Empty(Paths("--where", "sources.id"));
    }

    [Fact]
    public void Where_brackets_on_a_list_mean_the_same_as_none()
    {
        _workspace.Write("a.md", "---\nrelated: [x, y]\n---\n");
        _workspace.Write("b.md", "---\nrelated: [y]\n---\n");

        Assert.Equal(["a.md"], Paths("--where", "related[]=x"));
        Assert.Equal(Paths("--where", "related=x"), Paths("--where", "related[]=x"));
    }

    [Fact]
    public void Where_compares_a_field_against_another_field_of_the_same_page()
    {
        _workspace.Write("stale.md", "---\nverified:\n  at: 2026-08-01\ngenerated:\n  at: 2026-09-01\n---\n");
        _workspace.Write("fresh.md", "---\nverified:\n  at: 2026-10-01\ngenerated:\n  at: 2026-09-01\n---\n");
        _workspace.Write("unverified.md", "---\ngenerated:\n  at: 2026-09-01\n---\n");

        Assert.Equal(["stale.md"], Paths("--where", "verified.at<@generated.at"));
    }

    [Fact]
    public void Where_a_doubled_at_matches_a_literal_at()
    {
        _workspace.Write("handle.md", "---\nauthor: \"@alice\"\n---\n");
        _workspace.Write("name.md", "---\nauthor: alice\n---\n");

        Assert.Equal(["handle.md"], Paths("--where", "author=@@alice"));
    }

    [Theory]
    [InlineData("sources[.id=x")]
    [InlineData("sources[=x")]
    [InlineData("a=@")]
    [InlineData("a<@b[")]
    public void A_where_with_an_unclosed_bracket_or_a_malformed_reference_is_an_error(string condition)
    {
        var result = _workspace.Run("find", "--where", condition);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Where_help_says_only_pages_match_and_a_value_without_at_is_a_literal()
    {
        var text = Words(_workspace.Run("find", "--help").Stdout);

        Assert.Contains("Only markdown files", text);
        Assert.Contains("a value without a leading @ is always a literal", text);
    }

    [Theory]
    [InlineData("a!b")]
    [InlineData("!a=b")]
    [InlineData("a..b<3")]
    public void A_where_with_a_malformed_operator_or_field_is_an_error(string condition)
    {
        var result = _workspace.Run("find", "--where", condition);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public void Where_help_shows_the_condition_quoted_for_the_shell()
    {
        var result = _workspace.Run("find", "--help");

        Assert.Contains("'as_of<2026-04-01'", Words(result.Stdout));
    }

    [Fact]
    public void Field_json_gives_each_file_the_fields_asked_for_keyed_as_asked()
    {
        _workspace.Write("wiki/x.md", "---\nas_of: 2026-04-01\ntracking: open\nverified:\n  at: 2026-09-01\n  by: me\n---\n");

        var file = Assert.Single(Json(_workspace.Run("find", "--field", "as_of,tracking", "--field", "verified.at", "--json")).EnumerateArray());

        Assert.Equal("""{"as_of":"2026-04-01","tracking":"open","verified.at":"2026-09-01"}""", Compact(file.GetProperty("fields")));
    }

    [Fact]
    public void Field_json_leaves_out_a_missing_field_keeps_a_null_one_and_returns_a_list_whole()
    {
        _workspace.Write("x.md", "---\nas_of:\nsources:\n  - id: a\n  - id: b\n---\n");

        var file = Assert.Single(Json(_workspace.Run("find", "--field", "as_of,tracking,sources", "--json")).EnumerateArray());

        Assert.Equal("""{"as_of":null,"sources":[{"id":"a"},{"id":"b"}]}""", Compact(file.GetProperty("fields")));
    }

    [Fact]
    public void Field_json_gives_a_page_whose_frontmatter_failed_to_parse_an_empty_map_and_its_error()
    {
        _workspace.Write("bad.md", "---\nas_of: [unclosed\n---\n");

        var file = Assert.Single(Json(_workspace.Run("find", "--field", "as_of", "--json")).EnumerateArray());

        Assert.Equal("{}", Compact(file.GetProperty("fields")));
        Assert.NotEqual(JsonValueKind.Null, file.GetProperty("parseError").ValueKind);
    }

    [Fact]
    public void With_a_query_field_json_gives_each_page_its_fields()
    {
        _workspace.Write("a.md", "---\nas_of: 2026-04-01\n---\nkestrel\n");

        var file = Assert.Single(Json(_workspace.Run("find", "kestrel", "--field", "as_of", "--json")).EnumerateArray());

        Assert.Equal("""{"as_of":"2026-04-01"}""", Compact(file.GetProperty("fields")));
    }

    [Fact]
    public void Without_field_json_has_no_fields()
    {
        _workspace.Write("a.md", "---\nas_of: 2026-04-01\n---\nkestrel\n");

        Assert.False(Assert.Single(Json(_workspace.Run("find", "--json")).EnumerateArray()).TryGetProperty("fields", out _));
        Assert.False(Assert.Single(Json(_workspace.Run("find", "kestrel", "--json")).EnumerateArray()).TryGetProperty("fields", out _));
    }

    [Fact]
    public void Field_text_puts_each_field_on_the_line_lists_and_mappings_as_compact_json()
    {
        _workspace.Write("wiki/x.md", "---\nas_of: 2026-04-01\ntracking: open\ntags: [a, b]\nverified:\n  at: 2026-09-01\n---\n");
        _workspace.Write("wiki/y.md", "---\ntitle: Y\n---\n");

        var result = _workspace.Run("find", "--field", "as_of,tracking,tags,verified");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["wiki/x.md  as_of=2026-04-01  tracking=open  tags=[\"a\",\"b\"]  verified={\"at\":\"2026-09-01\"}", "wiki/y.md"],
            Lines(result.Stdout));
    }

    [Fact]
    public void With_a_query_field_text_goes_on_the_title_line_and_the_snippet_stays_below()
    {
        _workspace.Write("a.md", "---\ntitle: Alpha\nas_of: 2026-04-01\ncount: 3\ndraft: false\n---\nkestrel\n");

        var result = _workspace.Run("find", "kestrel", "--field", "as_of,count,draft");

        Assert.Equal(["a.md  Alpha  as_of=2026-04-01  count=3  draft=false", "kestrel"], Lines(result.Stdout));
    }

    [Fact]
    public void Field_json_through_brackets_gives_the_list_of_values()
    {
        _workspace.Write("x.md", "---\nsources:\n  - id: a\n    resource: raw/a.md\n  - id: b\n  - id: c\n    resource: raw/c.md\n---\n");

        var file = Assert.Single(Json(_workspace.Run("find", "--field", "sources[].resource,sources.resource", "--json")).EnumerateArray());

        Assert.Equal("""{"sources[].resource":["raw/a.md","raw/c.md"]}""", Compact(file.GetProperty("fields")));
    }

    [Fact]
    public void Field_text_through_brackets_shows_the_list_as_compact_json()
    {
        _workspace.Write("x.md", "---\nsources:\n  - resource: raw/a.md\n  - resource: raw/c.md\n---\n");

        var result = _workspace.Run("find", "--field", "sources[].resource");

        Assert.Equal(["x.md  sources[].resource=[\"raw/a.md\",\"raw/c.md\"]"], Lines(result.Stdout));
    }

    [Theory]
    [InlineData("sources[")]
    [InlineData("sources[.resource")]
    public void A_field_with_an_unclosed_bracket_is_an_error(string field)
    {
        var result = _workspace.Run("find", "--field", field);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--field", result.Stderr);
    }

    [Theory]
    [InlineData("as_of,")]
    [InlineData("a..b")]
    public void A_malformed_field_is_an_error(string field)
    {
        var result = _workspace.Run("find", "--field", field);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--field", result.Stderr);
    }

    [Fact]
    public void Field_help_says_which_keys_cannot_be_reached()
    {
        var result = _workspace.Run("find", "--help");

        Assert.Contains("keys holding ., ,, [ or ] cannot be reached", Words(result.Stdout));
    }

    [Fact]
    public void Field_text_escapes_control_characters_and_stays_on_one_line()
    {
        _workspace.Write("a.md", "---\nsummary: \"one\\ntwo\"\nnote: \"\\e]0;T\\a\"\n---\n");

        var result = _workspace.Run("find", "--field", "summary,note");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["a.md  summary=one\\x0atwo  note=\\x1b]0;T\\x07"], Lines(result.Stdout));
    }

    [Fact]
    public void Field_json_emits_a_field_nested_as_deeply_as_the_index_keeps()
    {
        _workspace.Write("deep.md", $"---\na: {new string('[', 60)}{new string(']', 60)}\n---\n");

        var result = _workspace.Run("find", "--field", "a", "--json");

        Assert.Equal(0, result.ExitCode);
        var json = JsonDocument.Parse(result.Stdout).RootElement.GetProperty("files");
        Assert.Equal(JsonValueKind.Array, json[0].GetProperty("fields").GetProperty("a").ValueKind);
    }

    private static string Compact(JsonElement json) => JsonSerializer.Serialize(json);

    /// <summary>Help text with each run of whitespace read as one space, so where it wraps does not matter.</summary>
    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    /// <summary>The whole object <c>find --json</c> prints, which holds <c>truncated</c> beside <c>files</c>.</summary>
    private JsonElement Found(params string[] args)
    {
        var result = _workspace.Run(["find", .. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        Assert.Equal("", result.Stderr);
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private void WritePages(int count, string word, string folder = "")
    {
        for (var i = 0; i < count; i++)
        {
            _workspace.Write($"{folder}p{i:d2}.md", $"{word}\n");
        }
    }

    [Fact]
    public void A_query_cut_off_at_the_default_limit_is_truncated()
    {
        WritePages(21, "heron");

        var found = Found("heron");

        Assert.Equal(20, found.GetProperty("files").GetArrayLength());
        Assert.True(found.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void A_query_cut_off_in_text_says_so_on_stderr_and_leaves_stdout_alone()
    {
        WritePages(21, "heron");

        var result = _workspace.Run("find", "heron");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(40, Lines(result.Stdout).Length);
        Assert.DoesNotContain("hippo:", result.Stdout);
        Assert.Equal("hippo: showing the first 20 matches; --limit raises the cap\n", result.Stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_query_with_exactly_as_many_matches_as_the_limit_is_not_truncated()
    {
        WritePages(20, "heron");

        var found = Found("heron");
        var text = _workspace.Run("find", "heron");

        Assert.Equal(20, found.GetProperty("files").GetArrayLength());
        Assert.False(found.GetProperty("truncated").GetBoolean());
        Assert.Equal((0, ""), (text.ExitCode, text.Stderr));
    }

    [Fact]
    public void A_listing_cut_off_by_limit_is_truncated()
    {
        WritePages(3, "heron");

        var found = Found("--limit", "2");

        Assert.Equal(["p00.md", "p01.md"], found.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.True(found.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void A_listing_cut_off_in_text_says_so_on_stderr()
    {
        WritePages(3, "heron");

        var result = _workspace.Run("find", "--limit", "2");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["p00.md", "p01.md"], Lines(result.Stdout));
        Assert.Equal("hippo: showing the first 2 files; --limit raises the cap\n", result.Stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_listing_with_exactly_as_many_kept_files_as_the_limit_is_not_truncated()
    {
        WritePages(3, "heron", "wiki/");
        WritePages(2, "heron", "raw/");

        var found = Found("--glob", "wiki/**", "--limit", "3");
        var text = _workspace.Run("find", "--glob", "wiki/**", "--limit", "3");

        Assert.Equal(3, found.GetProperty("files").GetArrayLength());
        Assert.False(found.GetProperty("truncated").GetBoolean());
        Assert.Equal((0, ""), (text.ExitCode, text.Stderr));
    }

    [Fact]
    public void A_query_cut_off_by_limit_is_truncated()
    {
        WritePages(3, "heron");

        var found = Found("heron", "--limit", "2");
        var text = _workspace.Run("find", "heron", "--limit", "2");

        Assert.Equal(2, found.GetProperty("files").GetArrayLength());
        Assert.True(found.GetProperty("truncated").GetBoolean());
        Assert.Equal("hippo: showing the first 2 matches; --limit raises the cap\n", text.Stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_query_within_a_limit_above_the_default_is_not_truncated()
    {
        WritePages(25, "heron");

        var found = Found("heron", "--limit", "30");
        var text = _workspace.Run("find", "heron", "--limit", "30");

        Assert.Equal(25, found.GetProperty("files").GetArrayLength());
        Assert.False(found.GetProperty("truncated").GetBoolean());
        Assert.Equal((0, ""), (text.ExitCode, text.Stderr));
    }

    [Fact]
    public void A_listing_without_limit_is_never_truncated()
    {
        WritePages(30, "heron");

        var found = Found();

        Assert.Equal(30, found.GetProperty("files").GetArrayLength());
        Assert.False(found.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Truncated_counts_only_what_the_filters_keep()
    {
        WritePages(20, "heron", "wiki/");
        WritePages(10, "heron", "raw/");

        var found = Found("heron", "--glob", "wiki/**");

        Assert.Equal(20, found.GetProperty("files").GetArrayLength());
        Assert.False(found.GetProperty("truncated").GetBoolean());
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

    [Fact]
    public void A_limit_that_is_not_a_number_is_an_error()
    {
        var result = _workspace.Run("find", "heron", "--limit", "nope");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Cannot parse argument 'nope' for option '--limit'", result.Stderr);
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
    public void No_backrefs_from_counts_only_links_whose_source_matches()
    {
        WriteGraph();

        // wiki/a.md is linked from wiki/c.md and wiki/index.md, so leaving both out of --from leaves it unlinked.
        Assert.Equal(["img.png", "raw/x.md", "wiki/a.md", "wiki/b.md", "wiki/c.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-backrefs", "--from", "raw/**"));
        Assert.Equal(["raw/x.md", "wiki/a.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-backrefs", "--from", "wiki/a.md", "--from", "wiki/b.md", "--from", "wiki/gallery.md"));
    }

    [Fact]
    public void No_backrefs_from_with_only_exclusions_counts_links_from_every_other_file()
    {
        WriteGraph();

        Assert.Equal(["raw/x.md", "wiki/a.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-backrefs", "--from", "!wiki/c.md", "--from", "!wiki/index.md"));
        Assert.Equal(["raw/x.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-backrefs", "--from", "wiki/**", "--from", "!wiki/index.md"));
    }

    [Fact]
    public void No_backrefs_from_is_workspace_relative_from_a_subfolder()
    {
        _workspace.Write("wiki/topic.md", "[a](../raw/a.md)\n");
        _workspace.Write("raw/day.md", "[b](b.md)\n");
        _workspace.Write("raw/a.md", "# A\n");
        _workspace.Write("raw/b.md", "# B\n");

        var result = _workspace.RunIn(_workspace.Combine("raw"), "find", "--glob", "raw/*.md", "--no-backrefs", "--from", "wiki/**");

        Assert.Equal(["raw/b.md", "raw/day.md"], Lines(result.Stdout));
    }

    [Fact]
    public void No_backrefs_from_still_never_counts_a_files_links_to_itself()
    {
        WriteGraph();

        Assert.Contains("wiki/self.md", Paths("--no-backrefs", "--from", "wiki/self.md"));
    }

    [Fact]
    public void No_backrefs_link_kind_counts_only_links_of_that_kind()
    {
        WriteGraph();

        // wiki/c.md is linked only from wiki/b.md's frontmatter.
        Assert.Contains("wiki/c.md", Paths("--no-backrefs", "--link-kind", "body"));
        Assert.Equal(["img.png", "raw/x.md", "wiki/a.md", "wiki/b.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-backrefs", "--link-kind", "frontmatter"));
    }

    [Fact]
    public void No_refs_link_kind_counts_only_links_of_that_kind()
    {
        WriteGraph();

        // wiki/b.md links out only from its frontmatter; wiki/a.md, wiki/c.md, wiki/index.md and wiki/gallery.md only from their bodies.
        Assert.Equal(["img.png", "raw/x.md", "wiki/b.md", "wiki/loose.md", "wiki/self.md"], Paths("--no-refs", "--link-kind", "body"));
        Assert.Equal(["img.png", "raw/x.md", "wiki/a.md", "wiki/c.md", "wiki/gallery.md", "wiki/index.md", "wiki/loose.md", "wiki/self.md"],
            Paths("--no-refs", "--link-kind", "frontmatter"));
    }

    [Fact]
    public void Link_kind_other_than_body_or_frontmatter_is_an_error()
    {
        var result = _workspace.Run("find", "--no-refs", "--link-kind", "wikilink");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("wikilink", result.Stderr);
    }

    [Fact]
    public void From_without_no_backrefs_is_an_error()
    {
        _workspace.Write("a.md", "# A\n");

        var alone = _workspace.Run("find", "--from", "wiki/**");
        var withNoRefs = _workspace.Run("find", "--no-refs", "--from", "wiki/**");

        Assert.Equal(2, alone.ExitCode);
        Assert.Contains("--from needs --no-backrefs", alone.Stderr);
        Assert.Equal(2, withNoRefs.ExitCode);
        Assert.Contains("--from needs --no-backrefs", withNoRefs.Stderr);
    }

    [Fact]
    public void Link_kind_without_no_refs_or_no_backrefs_is_an_error()
    {
        _workspace.Write("a.md", "# A\n");

        var result = _workspace.Run("find", "--link-kind", "body");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--link-kind needs --no-refs or --no-backrefs", result.Stderr);
    }

    [Fact]
    public void Artifacts_no_wiki_page_cites_in_frontmatter_take_one_call()
    {
        _workspace.Write(".hippo/config.json", """
            {
              "bundles": ["wiki"],
              "links": { "frontmatter": [{ "field": "sources[].resource", "resolve": "bundle" }] }
            }
            """);
        _workspace.Write("wiki/topic.md", "---\nsources:\n  - id: a\n    resource: ../raw/artifacts/cited.pdf\n---\n[body](../raw/artifacts/body.pdf)\n");
        _workspace.Write("raw/journal/2026-09-01.md", "[cited](../artifacts/cited.pdf) [day](../artifacts/day.pdf)\n");
        _workspace.Write("raw/artifacts/cited.pdf", "pdf");
        _workspace.Write("raw/artifacts/body.pdf", "pdf");
        _workspace.Write("raw/artifacts/day.pdf", "pdf");

        // Every artifact has some inbound link, so --no-backrefs alone finds none of them.
        Assert.Empty(Paths("--glob", "raw/artifacts/**", "--no-backrefs"));
        Assert.Equal(["raw/artifacts/body.pdf", "raw/artifacts/day.pdf"],
            Paths("--glob", "raw/artifacts/**", "--no-backrefs", "--from", "wiki/**", "--link-kind", "frontmatter"));
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
        _workspace.RollBackIndexTo(3);

        Assert.Equal(["a.md"], Paths("heron"));
    }

    [Fact]
    public void An_index_from_before_descriptions_were_searched_finds_them_after_migrating()
    {
        _workspace.Write("a.md", "---\ndescription: Notes on the kestrel.\n---\nStriped animals.\n");
        _workspace.Settle();
        _workspace.RollBackIndexTo(6);

        var result = Json(_workspace.Run("find", "kestrel", "--json"));

        Assert.Equal("a.md", Assert.Single(result.EnumerateArray()).GetProperty("path").GetString());
    }

    [Fact]
    public void Control_characters_in_file_names_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        _workspace.Write("n\u001b]0;T\u0007.md", "# N\n");

        var result = _workspace.Run("find");

        Assert.Contains("n\\x1b]0;T\\x07.md", Lines(result.Stdout));
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public void Turning_gitignore_off_lists_ignored_files()
    {
        _workspace.Git("init");
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": [".git/**"], "gitignore": false } }""");
        _workspace.Write(".gitignore", "build/\n*.log\n");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("build/out.md", "# Out\n");
        _workspace.Write("build/deep/more.md", "# More\n");
        _workspace.Write("notes/b.md", "# B\n");
        _workspace.Write("notes/debug.log", "log");
        _workspace.Write("only-logs/x.log", "log");

        var files = _workspace.Run("find");

        Assert.Equal(
            [".gitignore", "a.md", "build/deep/more.md", "build/out.md", "notes/b.md", "notes/debug.log", "only-logs/x.log"],
            Lines(files.Stdout).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Outside_a_repository_git_is_not_run_and_nothing_is_warned()
    {
        _workspace.Write(".gitignore", "*.log\n");
        _workspace.Write("debug.log", "log");

        var files = _workspace.Run("find");

        Assert.Equal("", files.Stderr);
        Assert.Equal([".gitignore", "debug.log"], Lines(files.Stdout).Order(StringComparer.Ordinal));
    }
}
