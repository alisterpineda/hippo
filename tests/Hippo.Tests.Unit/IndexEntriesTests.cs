using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

public class IndexEntriesTests
{
    private static readonly PageSettings Okf = new(new LinkSettings(["kb"], []), ["kb"]);

    private static List<string> Entries(string path, string body, PageSettings? settings = null) =>
        Page.Parse(path, body, settings ?? Okf).Entries.Select(e => $"{e.Line} {e.Target} {e.Description ?? "(none)"}").ToList();

    [Fact]
    public void An_entry_is_a_list_item_that_opens_with_a_link()
    {
        Assert.Equal(
            ["4 kb/topics/a.md Alpha", "5 kb/b.md (none)", "7 kb/c.md Gamma"],
            Entries("kb/index.md", """
                ---
                okf_version: "0.2"
                ---
                * [A](topics/a.md) - Alpha
                - [B](/b.md)
                  1. Not [an entry](x.md)
                  2. [C](c.md) - Gamma
                """));
    }

    [Fact]
    public void Other_links_in_an_index_are_not_entries()
    {
        Assert.Empty(Entries("kb/index.md", """
            Current state: [Now](now.md)

            * See [A](a.md) - Alpha
            * ![Logo](logo.png) - A picture
            * <https://example.com/a.md>
            * [Site](https://example.com) - Elsewhere
            * [Top](#top) - Up
            * [Out](../../../out.md) - Above the workspace
            """));
    }

    [Theory]
    [InlineData("* [A](a.md) - Alpha", "Alpha")]
    [InlineData("* [A](a.md) – Alpha", "Alpha")]
    [InlineData("* [A](a.md) — Alpha", "Alpha")]
    [InlineData("* [A](a.md): Alpha", "Alpha")]
    [InlineData("* [A](a.md) Alpha", "Alpha")]
    [InlineData("* [A](a.md) - - Alpha", "- Alpha")]
    [InlineData("* [A](a.md) - *Alpha* \\& more", "*Alpha* \\& more")]
    [InlineData("* [A](a.md) -   Alpha,\n  wrapped   twice\n", "Alpha, wrapped twice")]
    [InlineData("* [A](a.md) -", "(none)")]
    public void A_description_is_the_text_after_the_link_and_its_separator_as_written(string entry, string description)
    {
        Assert.Equal([$"1 kb/a.md {description}"], Entries("kb/index.md", entry));
    }

    [Fact]
    public void Only_an_index_in_an_okf_bundle_has_entries()
    {
        const string body = "* [A](a.md) - Alpha\n";

        Assert.Single(Entries("kb/sub/index.md", body));
        Assert.Empty(Entries("kb/a-index.md", body));
        Assert.Empty(Entries("kb/index.md", body, new PageSettings(new LinkSettings(["kb"], []), [])));
        Assert.Empty(Entries("notes/index.md", body));
    }
}
