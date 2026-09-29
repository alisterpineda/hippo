using System.Text.Json;

namespace Hippo.Tests.E2E;

public sealed class LinkCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(HippoProcess.Result result, int exitCode = 0)
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
              "links": {
                "bundles": ["wiki"],
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
    public async Task Refs_lists_a_pages_links_with_their_class()
    {
        WriteNotes();

        var json = Json(await _workspace.RunAsync("refs", "wiki/topics/topic.md", "--json"));

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
    public async Task Refs_prints_one_link_per_line()
    {
        WriteNotes();

        var result = await _workspace.RunAsync("refs", "raw/journal/2026-09-01.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["3  body         file     raw/journal/img/photo 1.png"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Refs_of_a_path_not_in_the_index_is_an_error()
    {
        var result = await _workspace.RunAsync("refs", "missing.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing.md is not in the index", result.Stderr);
    }

    [Fact]
    public async Task Refs_of_a_path_outside_the_workspace_is_an_error()
    {
        var result = await _workspace.RunAsync("refs", "../outside.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("is not a file inside the workspace", result.Stderr);
    }

    [Fact]
    public async Task Backrefs_lists_the_links_into_a_file()
    {
        WriteNotes();

        var result = await _workspace.RunAsync("backrefs", "raw/journal/2026-09-01.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Backrefs_of_a_missing_file_shows_the_links_broken_on_it()
    {
        WriteNotes();

        var json = Json(await _workspace.RunAsync("backrefs", "raw/journal/gone.md", "--json"));

        var link = Assert.Single(json.EnumerateArray());
        Assert.Equal(("wiki/topics/topic.md", 7, "frontmatter"),
            (link.GetProperty("source").GetString(), link.GetProperty("line").GetInt32(), link.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Backrefs_kind_filters_links_by_kind()
    {
        WriteNotes();
        _workspace.Write("wiki/other.md", "[day](../raw/journal/2026-09-01.md)\n");

        var body = await _workspace.RunAsync("backrefs", "raw/journal/2026-09-01.md", "--kind", "body");
        var frontmatter = await _workspace.RunAsync("backrefs", "raw/journal/2026-09-01.md", "--kind", "frontmatter");

        Assert.Equal(["wiki/other.md:1  body         ../raw/journal/2026-09-01.md"], Lines(body.Stdout));
        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(frontmatter.Stdout));
    }

    [Fact]
    public async Task Backrefs_kind_other_than_body_or_frontmatter_is_a_usage_error()
    {
        var result = await _workspace.RunAsync("backrefs", "a.md", "--kind", "wikilink");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Backrefs_transitive_lists_every_file_that_reaches_the_path()
    {
        WriteNotes();

        var text = await _workspace.RunAsync("backrefs", "raw/journal/img/photo 1.png", "--transitive");
        var json = Json(await _workspace.RunAsync("backrefs", "raw/journal/img/photo 1.png", "--transitive", "--json"));

        // The photo ← the day entry ← the topic (frontmatter) ← the index, which the topic also links back to.
        Assert.Equal(["raw/journal/2026-09-01.md", "wiki/index.md", "wiki/topics/topic.md"], Lines(text.Stdout));
        Assert.Equal(["raw/journal/2026-09-01.md", "wiki/index.md", "wiki/topics/topic.md"],
            json.EnumerateArray().Select(s => s.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task Broken_lists_links_to_missing_files_and_exits_1()
    {
        WriteNotes();

        var result = await _workspace.RunAsync("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:7  frontmatter  ../raw/journal/gone.md -> raw/journal/gone.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Broken_json_lists_the_same_links()
    {
        WriteNotes();
        _workspace.Write("wiki/escape.md", "[out](../../outside.md)\n");

        var json = Json(await _workspace.RunAsync("broken", "--json"), exitCode: 1);

        Assert.Equal(
            ["wiki/escape.md 1 body ../../outside.md null", "wiki/topics/topic.md 7 frontmatter ../raw/journal/gone.md raw/journal/gone.md"],
            json.EnumerateArray().Select(l =>
                $"{l.GetProperty("source").GetString()} {l.GetProperty("line").GetInt32()} {l.GetProperty("kind").GetString()} {l.GetProperty("raw").GetString()} {l.GetProperty("target").GetString() ?? "null"}"));
    }

    [Fact]
    public async Task Creating_a_missing_target_fixes_its_links_on_the_next_command()
    {
        WriteNotes();
        await _workspace.RunAsync("index");

        _workspace.Write("raw/journal/gone.md", "# Back\n");
        var result = await _workspace.RunAsync("broken");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Deleting_a_target_breaks_its_links_on_the_next_command()
    {
        WriteNotes();
        await _workspace.RunAsync("index");

        File.Delete(_workspace.Combine("raw/journal/2026-09-01.md"));
        var result = await _workspace.RunAsync("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md -> raw/journal/2026-09-01.md", Lines(result.Stdout));
    }

    [Fact]
    public async Task An_edit_to_a_pages_links_shows_up_on_the_next_command()
    {
        var path = _workspace.Write("a.md", "[b](b.md)\n");
        _workspace.Write("b.md", "# B\n");
        await _workspace.RunAsync("index");

        File.WriteAllText(path, "[c](c.md) and [b](b.md)\n");
        var result = await _workspace.RunAsync("refs", "a.md");

        Assert.Equal(["1  body         missing  c.md", "1  body         file     b.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Changing_the_link_settings_shows_up_on_the_next_command()
    {
        _workspace.Write("wiki/a.md", "[b](/b.md)\n");
        _workspace.Write("wiki/b.md", "# B\n");
        await _workspace.RunAsync("index");

        _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["wiki"] } }""");
        var result = await _workspace.RunAsync("refs", "wiki/a.md");

        Assert.Equal(["1  body         file     wiki/b.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Orphans_lists_pages_with_no_link_in_or_out_and_exits_1()
    {
        WriteNotes();

        var result = await _workspace.RunAsync("orphans");
        var json = Json(await _workspace.RunAsync("orphans", "--json"), exitCode: 1);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["raw/journal/lonely.md"], Lines(result.Stdout));
        Assert.Equal(["raw/journal/lonely.md"], json.EnumerateArray().Select(o => o.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Clean_workspaces_exit_0_from_broken_and_orphans()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");

        var broken = await _workspace.RunAsync("broken");
        var orphans = await _workspace.RunAsync("orphans", "--json");

        Assert.Equal((0, ""), (broken.ExitCode, broken.Stdout));
        Assert.Equal(0, orphans.ExitCode);
        Assert.Equal(0, Json(orphans).GetArrayLength());
    }

    [Fact]
    public async Task A_page_that_only_links_out_is_not_an_orphan_but_one_linking_only_outside_the_index_is()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("web.md", "[web](https://example.com) and [gone](gone.md)\n");

        var orphans = await _workspace.RunAsync("orphans");

        Assert.Equal(["web.md"], Lines(orphans.Stdout));
    }

    [Fact]
    public async Task Broken_names_a_link_to_the_workspace_root_as_such()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[home](./)\n");

        var result = await _workspace.RunAsync("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  body         ./ -> the workspace root"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Broken_names_a_link_that_leaves_the_workspace_as_such()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[out](../outside.md)\n");

        var result = await _workspace.RunAsync("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  body         ../outside.md -> outside the workspace"], Lines(result.Stdout));
    }

    [Fact]
    public async Task A_malformed_link_setting_is_an_error()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "a", "resolve": "folder" }] } }""");

        var result = await _workspace.RunAsync("broken");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("links.frontmatter[].resolve", result.Stderr);
    }

    [Fact]
    public async Task Link_commands_never_change_the_workspace()
    {
        WriteNotes();
        var before = Snapshot();

        foreach (var args in new[] { ["refs", "wiki/index.md"], ["backrefs", "wiki/index.md", "--transitive"], ["broken"], new[] { "orphans" } })
        {
            await _workspace.RunAsync(args);
        }

        Assert.Equal(before, Snapshot());
    }

    private string Snapshot() => string.Join('\n', Directory
        .EnumerateFileSystemEntries(_workspace.Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p) ? $"{p} {File.GetLastWriteTimeUtc(p).Ticks} {File.ReadAllText(p)}" : $"{p}/"));
}
