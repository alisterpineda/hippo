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
    public void Backrefs_kind_filters_links_by_kind()
    {
        WriteNotes();
        _workspace.Write("wiki/other.md", "[day](../raw/journal/2026-09-01.md)\n");

        var body = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--kind", "body");
        var frontmatter = _workspace.Run("backrefs", "raw/journal/2026-09-01.md", "--kind", "frontmatter");

        Assert.Equal(["wiki/other.md:1  body         ../raw/journal/2026-09-01.md"], Lines(body.Stdout));
        Assert.Equal(["wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md"], Lines(frontmatter.Stdout));
    }

    [Fact]
    public void Backrefs_kind_other_than_body_or_frontmatter_is_a_usage_error()
    {
        var result = _workspace.Run("backrefs", "a.md", "--kind", "wikilink");

        Assert.Equal(2, result.ExitCode);
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
    public void Broken_lists_links_to_missing_files_and_exits_1()
    {
        WriteNotes();

        var result = _workspace.Run("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:7  frontmatter  ../raw/journal/gone.md -> raw/journal/gone.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Broken_json_lists_the_same_links()
    {
        WriteNotes();
        _workspace.Write("wiki/escape.md", "[out](../../outside.md)\n");

        var json = Json(_workspace.Run("broken", "--json"), exitCode: 1);

        Assert.Equal(
            ["wiki/escape.md 1 body ../../outside.md null", "wiki/topics/topic.md 7 frontmatter ../raw/journal/gone.md raw/journal/gone.md"],
            json.EnumerateArray().Select(l =>
                $"{l.GetProperty("source").GetString()} {l.GetProperty("line").GetInt32()} {l.GetProperty("kind").GetString()} {l.GetProperty("raw").GetString()} {l.GetProperty("target").GetString() ?? "null"}"));
    }

    [Fact]
    public void Creating_a_missing_target_fixes_its_links_on_the_next_command()
    {
        WriteNotes();
        _workspace.Run("index");

        _workspace.Write("raw/journal/gone.md", "# Back\n");
        var result = _workspace.Run("broken");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void Deleting_a_target_breaks_its_links_on_the_next_command()
    {
        WriteNotes();
        _workspace.Run("index");

        File.Delete(_workspace.Combine("raw/journal/2026-09-01.md"));
        var result = _workspace.Run("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("wiki/topics/topic.md:5  frontmatter  ../raw/journal/2026-09-01.md -> raw/journal/2026-09-01.md", Lines(result.Stdout));
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

        _workspace.Write(".hippo/config.json", """{ "links": { "bundles": ["wiki"] } }""");
        var result = _workspace.Run("refs", "wiki/a.md");

        Assert.Equal(["1  body         file       wiki/b.md"], Lines(result.Stdout));
    }

    [Fact]
    public void Orphans_lists_files_with_no_link_in_or_out_and_exits_1()
    {
        WriteNotes();

        var result = _workspace.Run("orphans");
        var json = Json(_workspace.Run("orphans", "--json"), exitCode: 1);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["raw/journal/lonely.md"], Lines(result.Stdout));
        Assert.Equal(["raw/journal/lonely.md"], json.EnumerateArray().Select(o => o.GetProperty("path").GetString()));
    }

    [Fact]
    public void Clean_workspaces_exit_0_from_broken_and_orphans()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");

        var broken = _workspace.Run("broken");
        var orphans = _workspace.Run("orphans", "--json");

        Assert.Equal((0, ""), (broken.ExitCode, broken.Stdout));
        Assert.Equal(0, orphans.ExitCode);
        Assert.Equal(0, Json(orphans).GetArrayLength());
    }

    [Fact]
    public void A_page_that_only_links_out_is_not_an_orphan_but_one_linking_only_outside_the_index_is()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("web.md", "[web](https://example.com) and [gone](gone.md)\n");

        var orphans = _workspace.Run("orphans");

        Assert.Equal(["web.md"], Lines(orphans.Stdout));
    }

    [Fact]
    public void A_file_that_is_not_a_page_is_an_orphan_when_nothing_links_to_it()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "![used](used.png)\n");
        _workspace.Write("used.png", "png");
        _workspace.Write("unused.png", "png");

        var orphans = _workspace.Run("orphans");

        Assert.Equal(["unused.png"], Lines(orphans.Stdout));
    }

    [Fact]
    public void Orphans_exclude_hides_each_pattern_given_and_exits_0_when_none_are_left()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("README.md", "# Readme\n");
        _workspace.Write("archive/2020/old.md", "# Old\n");
        _workspace.Write("loose.md", "# Loose\n");

        var one = _workspace.Run("orphans", "--exclude", "archive/**");
        var both = Json(_workspace.Run("orphans", "--exclude", "archive/**", "--exclude", "*.md", "--json"));

        Assert.Equal(1, one.ExitCode);
        Assert.Equal(["README.md", "loose.md"], Lines(one.Stdout));
        Assert.Equal(0, both.GetArrayLength());
    }

    [Fact]
    public void Orphans_exclude_patterns_are_workspace_relative_from_a_subfolder()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("archive/old.md", "# Old\n");
        _workspace.Write("notes/loose.md", "# Loose\n");

        var result = _workspace.RunIn(_workspace.Combine("notes"), "orphans", "--exclude", "archive/**");

        Assert.Equal(["notes/loose.md"], Lines(result.Stdout));
    }

    [Fact]
    public void An_excluded_files_links_still_keep_what_they_link_to_from_being_orphans()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("archive/old.md", "[note](../note.md)\n");
        _workspace.Write("note.md", "# Note\n");

        var result = _workspace.Run("orphans", "--exclude", "archive/**");

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
    }

    [Fact]
    public void A_link_into_the_roots_hippo_folder_is_broken()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[config](.hippo/config.json)\n");

        var broken = _workspace.Run("broken");
        var orphans = _workspace.Run("orphans");

        Assert.Equal(["index.md:1  body         .hippo/config.json -> .hippo/config.json"], Lines(broken.Stdout));
        Assert.Equal(["index.md"], Lines(orphans.Stdout));
    }

    [Fact]
    public void A_link_to_a_folder_holding_an_indexed_file_is_a_directory_and_not_broken()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("wiki/index.md", "[home](../) [2021](../raw/2021/) [2021](../raw/2021) [empty](../raw/empty/)\n");
        _workspace.Write("raw/2021/day.md", "# Day\n");
        Directory.CreateDirectory(_workspace.Combine("raw/empty"));

        var refs = _workspace.Run("refs", "wiki/index.md");
        var broken = _workspace.Run("broken");

        Assert.Equal(
            ["1  body         directory  ../", "1  body         directory  raw/2021", "1  body         directory  raw/2021", "1  body         missing    raw/empty"],
            Lines(refs.Stdout));
        Assert.Equal(1, broken.ExitCode);
        Assert.Equal(["wiki/index.md:1  body         ../raw/empty/ -> raw/empty"], Lines(broken.Stdout));
    }

    [Fact]
    public void A_folder_link_breaks_once_the_last_indexed_file_under_it_is_gone()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[2021](raw/2021/)\n");
        _workspace.Write("raw/2021/01/day.md", "# Day\n");
        _workspace.Run("index");

        File.Delete(_workspace.Combine("raw/2021/01/day.md"));
        var result = _workspace.Run("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  body         raw/2021/ -> raw/2021"], Lines(result.Stdout));
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
    public void A_page_linking_only_to_a_folder_is_an_orphan()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");
        _workspace.Write("folders.md", "[here](./)\n");

        var orphans = _workspace.Run("orphans");

        Assert.Equal(["folders.md"], Lines(orphans.Stdout));
    }

    [Fact]
    public void Broken_names_a_link_that_leaves_the_workspace_as_such()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[out](../outside.md)\n");

        var result = _workspace.Run("broken");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  body         ../outside.md -> outside the workspace"], Lines(result.Stdout));
    }

    [Fact]
    public void A_malformed_link_setting_is_an_error()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "a", "resolve": "folder" }] } }""");

        var result = _workspace.Run("broken");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("links.frontmatter[].resolve", result.Stderr);
    }

    [Fact]
    public void Link_commands_never_change_the_workspace()
    {
        WriteNotes();
        var before = Snapshot();

        foreach (var args in new[] { ["refs", "wiki/index.md"], ["backrefs", "wiki/index.md", "--transitive"], ["broken"], new[] { "orphans" } })
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
