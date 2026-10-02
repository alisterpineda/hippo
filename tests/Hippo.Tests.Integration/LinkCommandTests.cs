using System.Text.Json;

namespace Hippo.Tests.Integration;

public sealed class LinkCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result, int exitCode = 0)
    {
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A small workspace in the notes repo's shape: a wiki bundle whose pages cite raw files from frontmatter.</summary>
    private void WriteNotes()
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
    }

    [Fact]
    public void Refs_lists_a_pages_links_with_their_class()
    {
        WriteNotes();

        var json = Json(_workspace.Run("refs", "wiki/topics/topic.md", "--json"));

        Assert.Equal(
            [
                "5 frontmatter file ../raw/journal/2026-09-01.md raw/journal/2026-09-01.md",
                "7 frontmatter missing ../raw/journal/gone.md raw/journal/gone.md",
                "11 body file /index.md wiki/index.md",
                "11 body url https://example.com ",
            ],
            json.EnumerateArray().Select(l =>
                $"{l.GetProperty("line").GetInt32()} {l.GetProperty("kind").GetString()} {l.GetProperty("type").GetString()} {l.GetProperty("raw").GetString()} {l.GetProperty("target").GetString()}"));
    }

    [Fact]
    public void Refs_prints_one_link_per_line()
    {
        WriteNotes();

        var result = _workspace.Run("refs", "raw/journal/2026-09-01.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["3  body         file       raw/journal/img/photo 1.png"], Lines(result.Stdout));
    }

    [Fact]
    public void Refs_of_a_path_not_in_the_index_is_an_error()
    {
        var result = _workspace.Run("refs", "missing.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing.md is not in the index", result.Stderr);
    }

    [Fact]
    public void Refs_of_a_path_outside_the_workspace_is_an_error()
    {
        var result = _workspace.Run("refs", "../outside.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("is not a file inside the workspace", result.Stderr);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("../outside.md")]
    public void Backrefs_of_a_path_outside_the_workspace_is_an_error(string path)
    {
        var result = _workspace.Run("backrefs", path);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("is not a file inside the workspace", result.Stderr);
    }

    [Fact]
    public void Backrefs_lists_the_links_into_a_file()
    {
        WriteNotes();

        var result = _workspace.Run("backrefs", "raw/journal/2026-09-01.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Backrefs_of_a_missing_file_shows_the_links_broken_on_it()
    {
        WriteNotes();

        var json = Json(_workspace.Run("backrefs", "raw/journal/gone.md", "--json"));

        var link = Assert.Single(json.EnumerateArray());
        Assert.Equal(("wiki/topics/topic.md", 7, "frontmatter", "../raw/journal/gone.md"),
            (link.GetProperty("source").GetString(), link.GetProperty("line").GetInt32(), link.GetProperty("kind").GetString(),
                link.GetProperty("raw").GetString()));
    }

    [Fact]
    public void Backrefs_link_kind_filters_links_by_kind()
    {
        WriteNotes();
        _workspace.Write("wiki/other.md", "[day](../raw/journal/2026-09-01.md)\n");

        var body = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--link-kind", "body");
        var frontmatter = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--link-kind", "frontmatter");

        Assert.Equal(["wiki/other.md:1  body         ../raw/journal/2026-09-01.md"], Lines(body.Stdout));
        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(frontmatter.Stdout));
    }

    [Fact]
    public void Backrefs_link_kind_other_than_body_or_frontmatter_is_a_usage_error()
    {
        var result = _workspace.Run("backrefs", "a.md", "--link-kind", "wikilink");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Backrefs_rejects_kind_since_link_kind_filters_its_links()
    {
        WriteNotes();

        var result = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--kind", "body");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--kind", result.Stderr);
    }

    [Fact]
    public void Backrefs_from_lists_only_links_whose_source_matches()
    {
        WriteNotes();
        _workspace.Write("raw/journal/2026-09-02.md", "[yesterday](2026-09-01.md)\n");
        _workspace.Write("wiki/other.md", "[day](../raw/journal/2026-09-01.md)\n");

        var wiki = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--from", "wiki/**");
        var notOther = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--from", "!wiki/other.md");
        var both = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--from", "wiki/**", "--link-kind", "body");

        Assert.Equal(["wiki/other.md:1  body         ../raw/journal/2026-09-01.md", "wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"],
            Lines(wiki.Stdout));
        Assert.Equal(["raw/journal/2026-09-02.md:1  body         2026-09-01.md", "wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"],
            Lines(notOther.Stdout));
        Assert.Equal(["wiki/other.md:1  body         ../raw/journal/2026-09-01.md"], Lines(both.Stdout));
    }

    [Fact]
    public void Backrefs_from_is_workspace_relative_from_a_subfolder()
    {
        WriteNotes();

        var result = _workspace.RunIn(_workspace.Combine("raw"), "backrefs", "journal/2026-09-01.md", "--from", "wiki/**");

        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Backrefs_transitive_from_passes_only_through_matching_files()
    {
        _workspace.Write("wiki/a.md", "[day](../raw/journal/d.md)\n");
        _workspace.Write("raw/journal/d.md", "[pdf](../../x.pdf)\n");
        _workspace.Write("x.pdf", "pdf");

        var all = _workspace.Run("backrefs", "x.pdf", "--transitive");
        var wiki = _workspace.Run("backrefs", "x.pdf", "--transitive", "--from", "wiki/**");

        Assert.Equal(["raw/journal/d.md", "wiki/a.md"], Lines(all.Stdout));
        Assert.Equal((0, ""), (wiki.ExitCode, wiki.Stdout));
    }

    [Fact]
    public void Backrefs_transitive_link_kind_follows_only_links_of_that_kind()
    {
        WriteNotes();

        // The photo ← the day entry (body) ⇠ the topic (frontmatter): body links alone stop at the day entry.
        var result = _workspace.Run("backrefs", "raw/journal/img/photo 1.png", "--transitive", "--link-kind", "body");

        Assert.Equal(["raw/journal/2026-09-01.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Backrefs_transitive_lists_every_file_that_reaches_the_path()
    {
        WriteNotes();

        var text = _workspace.Run("backrefs", "raw/journal/img/photo 1.png", "--transitive");
        var json = Json(_workspace.Run("backrefs", "raw/journal/img/photo 1.png", "--transitive", "--json"));

        // The photo ← the day entry ← the topic (frontmatter) ← the index, which the topic also links back to.
        Assert.Equal(["raw/journal/2026-09-01.md", "wiki/index.md", "wiki/topics/topic.md"], Lines(text.Stdout));
        Assert.Equal(["raw/journal/2026-09-01.md", "wiki/index.md", "wiki/topics/topic.md"],
            json.EnumerateArray().Select(s => s.GetProperty("source").GetString()));
    }

    [Fact]
    public void An_edit_to_a_pages_links_shows_up_on_the_next_command()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("b.md", "# B\n");
        _workspace.Run("index");

        File.WriteAllText(path, "[c](c.md) and [b](b.md)\n");
        var result = _workspace.Run("refs", "a.md");

        Assert.Equal(["1  body         missing    c.md", "1  body         file       b.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Changing_the_link_settings_shows_up_on_the_next_command()
    {
        _workspace.Write("wiki/a.md", "[b](/b.md)\n");
        _workspace.Write("wiki/b.md", "# B\n");
        _workspace.Run("index");

        _workspace.Write(".hippo/config.json", """{ "bundles": ["wiki"] }""");
        var result = _workspace.Run("refs", "wiki/a.md");

        Assert.Equal(["1  body         file       wiki/b.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Backrefs_of_a_folder_lists_the_links_to_it_with_or_without_a_trailing_slash()
    {
        WriteNotes();
        _workspace.Write("wiki/journal.md", "[journal](../raw/journal/) and [a day](../raw/journal/2026-09-01.md)\n");

        var bare = _workspace.Run("backrefs", "raw/journal");
        var slashed = _workspace.Run("backrefs", "raw/journal/");

        Assert.Equal(["wiki/journal.md:1  body         ../raw/journal/"], Lines(bare.Stdout));
        Assert.Equal(bare.Stdout, slashed.Stdout);
    }

    [Fact]
    public void Backrefs_of_the_workspace_root_lists_the_links_to_it()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[home](./) and [a](wiki/a.md)\n");
        _workspace.Write("wiki/a.md", "[home](../)\n");

        var fromRoot = _workspace.Run("backrefs", ".");
        var fromFolder = _workspace.RunIn(_workspace.Combine("wiki"), "backrefs", "..");
        var transitive = _workspace.Run("backrefs", ".", "--transitive");

        Assert.Equal(["index.md:1  body         ./", "wiki/a.md:1  body         ../"], Lines(fromRoot.Stdout));
        Assert.Equal(fromRoot.Stdout, fromFolder.Stdout);
        Assert.Equal(["index.md", "wiki/a.md"], Lines(transitive.Stdout));
    }

    [Fact]
    public void Refs_of_the_workspace_root_is_an_error()
    {
        var result = _workspace.Run("refs", ".");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("is not a file inside the workspace", result.Stderr);
    }

    [Fact]
    public void A_malformed_link_setting_is_an_error()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "a", "resolve": "folder" }] } }""");

        var result = _workspace.Run("find");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("links.frontmatter[].resolve", result.Stderr);
    }

    [Fact]
    public void Link_commands_never_change_the_workspace()
    {
        WriteNotes();
        var before = Snapshot();

        foreach (var args in new[] { ["refs", "wiki/index.md"], ["backrefs", "wiki/index.md", "--transitive"], new[] { "lint", "--rule", "broken-link" }, ["find", "--no-refs", "--no-backrefs"] })
        {
            _workspace.Run(args);
        }

        Assert.Equal(before, Snapshot());
    }

    private string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(_workspace.Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p) ? $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {File.ReadAllText(p)}" : $"{p}/"));
}
