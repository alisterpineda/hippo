using Hippo.Notebooks;

namespace Hippo.Tests.Unit;

public class PageTests
{
    private static readonly NotebookConfig NotesConfig = NotebookConfig.Parse("""
        bundles:
          - root: wiki
        links:
          body: { resolve: page }
          frontmatter:
            - field: sources[].resource
              resolve: bundle
            - field: generated.from
        """);

    private static List<Link> Links(string path, string text, NotebookConfig? config = null) =>
        Page.Parse(path, text, (config ?? NotesConfig).LinkSettings).Links;

    private static Link Only(string path, string text, NotebookConfig? config = null) => Assert.Single(Links(path, text, config));

    [Fact]
    public void An_inline_link_resolves_from_the_pages_folder()
    {
        Assert.Equal(new Link(1, "body", "path", "b.md", "wiki/topics/b.md"), Only("wiki/topics/a.md", "See [B](b.md).\n"));
    }

    [Fact]
    public void An_image_is_a_link()
    {
        Assert.Equal(new Link(1, "body", "path", "img/x.png", "notes/img/x.png"), Only("notes/a.md", "![alt](img/x.png)\n"));
    }

    [Fact]
    public void Reference_links_take_the_definitions_destination_and_the_line_they_are_used_on()
    {
        var links = Links("a.md", "# A\n\n[full][r] and [r][] and [r].\n\n[r]: target.md\n");

        Assert.Equal(3, links.Count);
        Assert.All(links, link => Assert.Equal(new Link(3, "body", "path", "target.md", "target.md"), link));
    }

    [Fact]
    public void A_reference_definition_nothing_uses_is_not_a_link()
    {
        Assert.Empty(Links("a.md", "[unused]: target.md\n"));
    }

    [Fact]
    public void An_angle_bracket_destination_keeps_its_spaces()
    {
        Assert.Equal("raw/my file.md", Only("raw/a.md", "[x](<my file.md>)\n").Target);
    }

    [Fact]
    public void A_percent_encoded_destination_is_decoded_but_its_raw_text_kept()
    {
        var link = Only("raw/a.md", "[x](my%20file%23.md)\n");

        Assert.Equal(("my%20file%23.md", "raw/my file#.md"), (link.Raw, link.Target));
    }

    [Fact]
    public void Fragment_and_query_are_not_part_of_the_target()
    {
        Assert.Equal("b.md", Only("a.md", "[x](b.md#heading)\n").Target);
        Assert.Equal("b.md", Only("a.md", "[x](b.md?plain=1#L3)\n").Target);
    }

    [Theory]
    [InlineData("[x](#heading)", "#heading")]
    [InlineData("[x]()", "")]
    public void A_link_within_the_page_is_anchor_only(string markdown, string raw)
    {
        Assert.Equal(new Link(1, "body", "anchor", raw, null), Only("a.md", markdown));
    }

    [Theory]
    [InlineData("[x](https://example.com/a.md)", "https://example.com/a.md")]
    [InlineData("[x](mailto:me@example.com)", "mailto:me@example.com")]
    [InlineData("<https://example.com>", "https://example.com")]
    [InlineData("<me@example.com>", "me@example.com")]
    [InlineData("[x](//example.com/a)", "//example.com/a")]
    public void A_link_with_a_scheme_is_a_url(string markdown, string raw)
    {
        Assert.Equal(new Link(1, "body", "url", raw, null), Only("a.md", markdown));
    }

    [Fact]
    public void Dot_segments_are_normalized()
    {
        Assert.Equal("raw/j/x.md", Only("wiki/topics/a.md", "[x](../../raw/./j/x.md)\n").Target);
    }

    [Fact]
    public void A_link_that_leaves_the_notebook_has_no_target()
    {
        Assert.Equal(new Link(1, "body", "path", "../../x.md", null), Only("wiki/a.md", "[x](../../x.md)\n"));
    }

    [Theory]
    [InlineData("index.md", "./")]
    [InlineData("raw/a.md", "/")]
    [InlineData("wiki/a.md", "../")]
    public void A_link_to_the_notebook_root_targets_the_empty_key_not_outside(string path, string destination)
    {
        Assert.Equal("", Only(path, $"[home]({destination})\n").Target);
    }

    [Fact]
    public void A_leading_slash_resolves_against_the_pages_bundle_root()
    {
        Assert.Equal("wiki/topics/b.md", Only("wiki/topics/a.md", "[x](/topics/b.md)\n").Target);
    }

    [Fact]
    public void A_leading_slash_outside_any_bundle_resolves_against_the_notebook_root()
    {
        Assert.Equal("topics/b.md", Only("raw/a.md", "[x](/topics/b.md)\n").Target);
    }

    [Fact]
    public void Body_links_can_resolve_from_the_bundle_root()
    {
        var config = NotebookConfig.Parse("bundles: [{root: wiki}]\nlinks:\n  body: {resolve: bundle}\n");

        Assert.Equal("wiki/b.md", Only("wiki/topics/a.md", "[x](b.md)\n", config).Target);
    }

    [Fact]
    public void The_deepest_bundle_holding_the_page_is_its_bundle()
    {
        var config = NotebookConfig.Parse("bundles: [{root: wiki}, {root: wiki/sub}]\n");

        Assert.Equal("wiki/sub/b.md", Only("wiki/sub/deep/a.md", "[x](/b.md)\n", config).Target);
        Assert.Equal("wiki/b.md", Only("wiki/subway/a.md", "[x](/b.md)\n", config).Target);
    }

    [Fact]
    public void Body_link_lines_count_from_the_top_of_the_file_and_frontmatter_text_is_not_a_body_link()
    {
        var link = Only("a.md", "---\ntitle: \"[not](a-link.md)\"\n---\n\n[b](b.md)\n");

        Assert.Equal((5, "b.md"), (link.Line, link.Target));
    }

    [Fact]
    public void Wikilinks_and_code_are_text()
    {
        Assert.Empty(Links("a.md", "[[b]] and `[c](c.md)`\n\n    [d](d.md)\n"));
    }

    [Fact]
    public void Frontmatter_list_fields_are_links_with_the_line_of_their_value()
    {
        var links = Links("wiki/topics/a.md", """
            ---
            title: A
            sources:
              - id: j-1
                resource: ../raw/j/1.md
              - id: u-x
                resource: https://example.com
            ---
            """);

        Assert.Equal(
            [new Link(5, "frontmatter", "path", "../raw/j/1.md", "raw/j/1.md"), new Link(7, "frontmatter", "url", "https://example.com", null)],
            links);
    }

    [Fact]
    public void Frontmatter_dotted_fields_follow_nested_mappings_and_resolve_from_the_page()
    {
        var link = Only("wiki/topics/a.md", "---\ngenerated:\n  at: 2026-09-01\n  from: b.md\n---\n");

        Assert.Equal(new Link(4, "frontmatter", "path", "b.md", "wiki/topics/b.md"), link);
    }

    [Fact]
    public void Frontmatter_values_are_literal_paths()
    {
        Assert.Equal("wiki/topics/a%20b.md", Only("wiki/topics/a.md", "---\ngenerated:\n  from: a%20b.md\n---\n").Target);
    }

    [Theory]
    [InlineData("sources: ../raw/j/1.md")]
    [InlineData("sources:\n  resource: ../raw/j/1.md")]
    [InlineData("sources:\n  - resource: [a.md, b.md]")]
    [InlineData("sources:\n  - resource:")]
    [InlineData("sources:\n  - resource: ~")]
    [InlineData("sources:\n  - resource: null")]
    [InlineData("generated:\n  from: [a.md]")]
    public void A_frontmatter_value_of_another_shape_is_not_a_link(string yaml)
    {
        Assert.Empty(Links("wiki/a.md", $"---\n{yaml}\n---\n"));
    }

    [Theory]
    [InlineData("\"null\"")]
    [InlineData("!!str null")]
    public void A_frontmatter_null_that_is_a_string_is_a_link(string value)
    {
        Assert.Equal("wiki/null", Only("wiki/a.md", $"---\nsources:\n  - resource: {value}\n---\n").Target);
    }

    [Fact]
    public void A_body_markdig_cannot_parse_is_an_error_and_keeps_the_frontmatter_links()
    {
        var text = $"---\ngenerated:\n  from: b.md\n---\n{new string('>', 200)} x\n";

        var page = Page.Parse("a.md", text, NotesConfig.LinkSettings);

        Assert.NotNull(page.BodyError);
        Assert.Equal("b.md", Assert.Single(page.Links).Target);
    }

    [Fact]
    public void Malformed_frontmatter_has_no_links_but_the_body_still_does()
    {
        var page = Page.Parse("a.md", "---\nsources: [\n---\n[b](b.md)\n", NotesConfig.LinkSettings);

        Assert.NotNull(page.Frontmatter.Error);
        Assert.Equal(new Link(4, "body", "path", "b.md", "b.md"), Assert.Single(page.Links));
    }

    [Fact]
    public void An_unclosed_frontmatter_block_leaves_the_whole_file_as_body()
    {
        var link = Only("a.md", "---\ntitle: A\n[b](b.md)\n");

        Assert.Equal((3, "b.md"), (link.Line, link.Target));
    }

    [Fact]
    public void Page_parse_returns_the_frontmatter_as_json()
    {
        var page = Page.Parse("a.md", "---\ntitle: A\n---\n", NotesConfig.LinkSettings);

        Assert.Equal("""{"title":"A"}""", page.Frontmatter.Json);
    }
}
