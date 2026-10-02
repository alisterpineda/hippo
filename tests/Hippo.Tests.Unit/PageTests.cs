using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

public class PageTests
{
    private static readonly WorkspaceConfig NotesConfig = WorkspaceConfig.Parse("""
        {
          "bundles": ["wiki"],
          "links": {
            "frontmatter": [
              { "field": "sources[].resource", "resolve": "bundle" },
              { "field": "generated.from" }
            ]
          }
        }
        """);

    private static readonly PageSettings Notes = new(NotesConfig.Bundles, NotesConfig.Links, []);

    private static List<Link> Links(string path, string text, WorkspaceConfig? config = null)
    {
        config ??= NotesConfig;
        return Page.Parse(path, text, new PageSettings(config.Bundles, config.Links, [])).Links;
    }

    private static Link Only(string path, string text, WorkspaceConfig? config = null) => Assert.Single(Links(path, text, config));

    [Fact]
    public void An_inline_link_resolves_from_the_pages_folder()
    {
        Assert.Equal(new Link(1, "body", "path", "b.md", "wiki/topics/b.md", "B"), Only("wiki/topics/a.md", "See [B](b.md).\n"));
    }

    [Fact]
    public void An_image_is_a_link()
    {
        Assert.Equal(new Link(1, "body", "path", "img/x.png", "notes/img/x.png", "alt"), Only("notes/a.md", "![alt](img/x.png)\n"));
    }

    [Fact]
    public void Reference_links_take_the_definitions_destination_and_the_line_they_are_used_on()
    {
        var links = Links("a.md", "# A\n\n[full][r] and [r][] and [r].\n\n[r]: target.md\n");

        Assert.Equal(3, links.Count);
        Assert.All(links, link => Assert.Equal((3, "body", "path", "target.md", "target.md"), (link.Line, link.Kind, link.Type, link.Raw, link.Target)));
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
        Assert.Equal(new Link(1, "body", "anchor", raw, null, "x"), Only("a.md", markdown));
    }

    [Theory]
    [InlineData("[x](https://example.com/a.md)", "https://example.com/a.md", "x")]
    [InlineData("[x](mailto:me@example.com)", "mailto:me@example.com", "x")]
    [InlineData("<https://example.com>", "https://example.com", "https://example.com")]
    [InlineData("<me@example.com>", "me@example.com", "me@example.com")]
    [InlineData("[x](//example.com/a)", "//example.com/a", "x")]
    public void A_link_with_a_scheme_is_a_url(string markdown, string raw, string text)
    {
        Assert.Equal(new Link(1, "body", "url", raw, null, text), Only("a.md", markdown));
    }

    [Fact]
    public void Dot_segments_are_normalized()
    {
        Assert.Equal("raw/j/x.md", Only("wiki/topics/a.md", "[x](../../raw/./j/x.md)\n").Target);
    }

    [Fact]
    public void A_link_that_leaves_the_workspace_has_no_target()
    {
        Assert.Equal(new Link(1, "body", "path", "../../x.md", null, "x"), Only("wiki/a.md", "[x](../../x.md)\n"));
    }

    [Theory]
    [InlineData("index.md", "./")]
    [InlineData("raw/a.md", "/")]
    [InlineData("wiki/a.md", "../")]
    public void A_link_to_the_workspace_root_targets_the_empty_key_not_outside(string path, string destination)
    {
        Assert.Equal("", Only(path, $"[home]({destination})\n").Target);
    }

    [Fact]
    public void A_leading_slash_resolves_against_the_pages_bundle_root()
    {
        Assert.Equal("wiki/topics/b.md", Only("wiki/topics/a.md", "[x](/topics/b.md)\n").Target);
    }

    [Fact]
    public void A_leading_slash_outside_any_bundle_resolves_against_the_workspace_root()
    {
        Assert.Equal("topics/b.md", Only("raw/a.md", "[x](/topics/b.md)\n").Target);
    }

    [Fact]
    public void The_deepest_bundle_holding_the_page_is_its_bundle()
    {
        var config = WorkspaceConfig.Parse("""{ "bundles": ["wiki", "wiki/sub"] }""");

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
            [new Link(5, "frontmatter", "path", "../raw/j/1.md", "raw/j/1.md", null), new Link(7, "frontmatter", "url", "https://example.com", null, null)],
            links);
    }

    [Fact]
    public void Frontmatter_dotted_fields_follow_nested_mappings_and_resolve_from_the_page()
    {
        var link = Only("wiki/topics/a.md", "---\ngenerated:\n  at: 2026-09-01\n  from: b.md\n---\n");

        Assert.Equal(new Link(4, "frontmatter", "path", "b.md", "wiki/topics/b.md", null), link);
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

        var page = Page.Parse("a.md", text, Notes);

        Assert.NotNull(page.BodyError);
        Assert.Equal("b.md", Assert.Single(page.Links).Target);
    }

    [Fact]
    public void A_body_markdig_cannot_parse_keeps_the_frontmatter_title()
    {
        var page = Page.Parse("a.md", $"---\ntitle: Deep\n---\n{new string('>', 200)} x\n", Notes);

        Assert.NotNull(page.BodyError);
        Assert.Equal("Deep", page.Title);
        Assert.Equal($"{new string('>', 200)} x\n", page.Body);
    }

    [Fact]
    public void Malformed_frontmatter_has_no_links_but_the_body_still_does()
    {
        var page = Page.Parse("a.md", "---\nsources: [\n---\n[b](b.md)\n", Notes);

        Assert.NotNull(page.Frontmatter.Error);
        Assert.Equal(new Link(4, "body", "path", "b.md", "b.md", "b"), Assert.Single(page.Links));
    }

    [Fact]
    public void An_unclosed_frontmatter_block_leaves_the_whole_file_as_body()
    {
        var link = Only("a.md", "---\ntitle: A\n[b](b.md)\n");

        Assert.Equal((3, "b.md"), (link.Line, link.Target));
    }

    [Theory]
    [InlineData("[**Okf** bundles](x.md)", "Okf bundles")]
    [InlineData("[the `--json` flag](x.md)", "the --json flag")]
    [InlineData("[Fish &amp; Chips](x.md)", "Fish & Chips")]
    [InlineData("[Herons\nand   egrets](x.md)", "Herons and egrets")]
    [InlineData("![a diagram](d.png)", "a diagram")]
    [InlineData("<https://x.org>", "https://x.org")]
    [InlineData("<me@example.com>", "me@example.com")]
    [InlineData("[](x.md)", "")]
    public void A_body_links_text_is_what_a_reader_sees(string markdown, string text)
    {
        Assert.Equal(text, Only("a.md", markdown + "\n").Text);
    }

    [Fact]
    public void A_link_around_an_image_has_the_images_alt_text_as_its_text()
    {
        Assert.Equal(["a badge", "a badge"], Links("a.md", "[![a badge](b.svg)](x.md)\n").Select(l => l.Text));
    }

    [Fact]
    public void A_reference_links_text_is_its_label_not_the_reference_id()
    {
        var links = Links("a.md", "[full label][r] and [r][] and [r].\n\n[r]: target.md\n");

        Assert.Equal(["full label", "r", "r"], links.Select(link => link.Text));
    }

    [Fact]
    public void A_frontmatter_link_has_no_text()
    {
        Assert.Null(Only("wiki/topics/a.md", "---\ngenerated:\n  from: b.md\n---\n").Text);
    }

    [Fact]
    public void Page_parse_returns_the_frontmatter_as_json()
    {
        var page = Page.Parse("a.md", "---\ntitle: A\n---\n", Notes);

        Assert.Equal("""{"title":"A"}""", page.Frontmatter.Json);
    }

    [Fact]
    public void The_title_is_the_frontmatter_title()
    {
        var page = Page.Parse("a.md", "---\ntitle: Frontmatter title\n---\n# Heading title\n", Notes);

        Assert.Equal("Frontmatter title", page.Title);
    }

    [Fact]
    public void Without_a_frontmatter_title_the_title_is_the_first_level_1_heading()
    {
        var page = Page.Parse("a.md", "---\ntype: Topic\n---\n## Not this\n\n# The *first* `one`\n\n# Not this either\n", Notes);

        Assert.Equal("The first one", page.Title);
    }

    [Theory]
    [InlineData("---\ntitle: \"\"\n---\n# Heading\n")]
    [InlineData("---\ntitle: ~\n---\n# Heading\n")]
    [InlineData("---\ntitle: [a, b]\n---\n# Heading\n")]
    [InlineData("---\ntitle: [\n---\n# Heading\n")]
    public void A_title_that_is_empty_not_text_or_unreadable_falls_back_to_the_heading(string text)
    {
        Assert.Equal("Heading", Page.Parse("a.md", text, Notes).Title);
    }

    [Theory]
    [InlineData("Herons\nand egrets\n===\n", "Herons and egrets")]
    [InlineData("# Fish &amp; Chips\n", "Fish & Chips")]
    [InlineData("# See <https://example.com/a>\n", "See https://example.com/a")]
    [InlineData("---\ntitle: |\n  Herons\n  and   egrets\n---\n", "Herons and egrets")]
    public void A_title_is_its_text_on_one_line(string text, string title)
    {
        Assert.Equal(title, Page.Parse("a.md", text, Notes).Title);
    }

    [Fact]
    public void A_page_with_no_title_or_level_1_heading_has_an_empty_title()
    {
        Assert.Equal("", Page.Parse("a.md", "## Section\n\nText.\n", Notes).Title);
    }

    [Fact]
    public void The_body_is_the_text_after_the_frontmatter()
    {
        var page = Page.Parse("a.md", "---\ntitle: A\n---\n# A\n\nSome [text](b.md).\n", Notes);

        Assert.Equal("# A\n\nSome [text](b.md).\n", page.Body);
    }

    [Fact]
    public void A_page_without_frontmatter_is_all_body()
    {
        Assert.Equal("Just text.\n", Page.Parse("a.md", "Just text.\n", Notes).Body);
    }
}
