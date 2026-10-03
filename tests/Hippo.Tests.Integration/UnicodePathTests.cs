using System.Text.Json;

namespace Hippo.Tests.Integration;

/// <summary>Two paths equal under NFC name the same file, in links and in the paths and globs a command is given, on
/// every OS. What is printed is never normalized: a file's path is its name on disk, and a link reads as written.</summary>
public sealed class UnicodePathTests : IDisposable
{
    /// <summary><c>café</c> with a precomposed <c>é</c>, as most tools write it.</summary>
    private const string Nfc = "café";

    /// <summary><c>café</c> as <c>e</c> and a combining acute accent, as a name dragged in from Finder arrives.</summary>
    private const string Nfd = "café";

    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result, int exitCode = 0)
    {
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private List<string> Paths(params string[] args) =>
        Json(_workspace.Run(["find", .. args, "--json"])).EnumerateArray().Select(r => r.GetProperty("path").GetString()!).ToList();

    /// <summary>A person page stored in NFC, and a note linking to it once in each form.</summary>
    private void WriteCafe()
    {
        _workspace.Write($"wiki/people/{Nfc}.md", "# Café\n");
        _workspace.Write("wiki/notes/a.md", $"[a](../people/{Nfd}.md)\n[b](../people/{Nfc}.md)\n");
    }

    [Fact]
    public void A_link_in_either_form_to_a_file_stored_in_nfc_is_a_file()
    {
        WriteCafe();

        var json = Json(_workspace.Run("refs", "wiki/notes/a.md", "--json"));

        Assert.Equal(["file", "file"], json.EnumerateArray().Select(l => l.GetProperty("type").GetString()));
    }

    [Fact]
    public void Backrefs_given_the_nfd_spelling_lists_the_links_in_both_forms()
    {
        WriteCafe();

        var result = _workspace.Run("backrefs", $"wiki/people/{Nfd}.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [$"wiki/notes/a.md:1  body         ../people/{Nfd}.md  [a]", $"wiki/notes/a.md:2  body         ../people/{Nfc}.md  [b]"],
            Lines(result.Stdout));
    }

    [Fact]
    public void Backrefs_given_the_nfc_spelling_lists_the_links_in_both_forms()
    {
        WriteCafe();

        var json = Json(_workspace.Run("backrefs", $"wiki/people/{Nfc}.md", "--json"));

        Assert.Equal([1, 2], json.EnumerateArray().Select(l => l.GetProperty("line").GetInt32()));
    }

    [Fact]
    public void A_link_in_nfc_reaches_a_file_stored_in_nfd()
    {
        _workspace.Write($"wiki/people/{Nfd}.md", "# Café\n");
        _workspace.Write("wiki/notes/a.md", $"[b](../people/{Nfc}.md)\n");

        var json = Json(_workspace.Run("refs", "wiki/notes/a.md", "--json"));

        Assert.Equal("file", Assert.Single(json.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public void A_frontmatter_link_in_nfd_reaches_a_file_stored_in_nfc()
    {
        _workspace.Write(".hippo/config.json", """{ "links": { "frontmatter": [{ "field": "person" }] } }""");
        _workspace.Write($"wiki/people/{Nfc}.md", "# Café\n");
        _workspace.Write("wiki/notes/a.md", $"---\nperson: ../people/{Nfd}.md\n---\n");

        var json = Json(_workspace.Run("refs", "wiki/notes/a.md", "--json"));

        Assert.Equal("file", Assert.Single(json.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public void Refs_shows_a_links_target_as_written()
    {
        WriteCafe();

        var json = Json(_workspace.Run("refs", "wiki/notes/a.md", "--json"));

        Assert.Equal([$"wiki/people/{Nfd}.md", $"wiki/people/{Nfc}.md"], json.EnumerateArray().Select(l => l.GetProperty("target").GetString()));
    }

    [Fact]
    public void Show_given_the_nfd_spelling_shows_the_file_under_its_name_on_disk()
    {
        WriteCafe();

        var json = Json(_workspace.Run("show", $"wiki/people/{Nfd}.md", "--json"));

        Assert.Equal($"wiki/people/{Nfc}.md", json.GetProperty("path").GetString());
    }

    [Fact]
    public void Refs_given_the_nfd_spelling_of_a_page_lists_its_links()
    {
        _workspace.Write($"wiki/{Nfc}.md", "[x](x.md)\n");
        _workspace.Write("wiki/x.md", "# X\n");

        var json = Json(_workspace.Run("refs", $"wiki/{Nfd}.md", "--json"));

        Assert.Equal("wiki/x.md", Assert.Single(json.EnumerateArray()).GetProperty("target").GetString());
    }

    [Fact]
    public void Backrefs_lists_each_source_under_its_name_on_disk()
    {
        _workspace.Write($"wiki/{Nfd}.md", "[x](x.md)\n");
        _workspace.Write("wiki/x.md", "# X\n");

        var json = Json(_workspace.Run("backrefs", "wiki/x.md", "--json"));

        Assert.Equal($"wiki/{Nfd}.md", Assert.Single(json.EnumerateArray()).GetProperty("source").GetString());
    }

    [Fact]
    public void Transitive_backrefs_follow_a_chain_through_links_in_either_form()
    {
        // b links to a in NFD; c links to b, whose name is stored in NFD, in NFC.
        _workspace.Write($"{Nfc}-a.md", "# A\n");
        _workspace.Write($"{Nfd}-b.md", $"[a]({Nfd}-a.md)\n");
        _workspace.Write("c.md", $"[b]({Nfc}-b.md)\n");

        var json = Json(_workspace.Run("backrefs", $"{Nfd}-a.md", "--transitive", "--json"));

        Assert.Equal(["c.md", $"{Nfd}-b.md"], json.EnumerateArray().Select(s => s.GetProperty("source").GetString()));
    }

    [Fact]
    public void Transitive_backrefs_given_the_nfc_spelling_list_each_source_under_its_name_on_disk()
    {
        // b, stored in NFC, links to a in NFD; c links to b in NFD.
        _workspace.Write($"{Nfd}-a.md", "# A\n");
        _workspace.Write($"{Nfc}-b.md", $"[a]({Nfd}-a.md)\n");
        _workspace.Write("c.md", $"[b]({Nfd}-b.md)\n");

        var json = Json(_workspace.Run("backrefs", $"{Nfc}-a.md", "--transitive", "--json"));

        Assert.Equal(["c.md", $"{Nfc}-b.md"], json.EnumerateArray().Select(s => s.GetProperty("source").GetString()));
    }

    [Fact]
    public void Find_lists_each_file_under_its_name_on_disk()
    {
        WriteCafe();

        Assert.Equal([$"wiki/people/{Nfc}.md"], Paths("--glob", "wiki/people/**"));
    }

    [Fact]
    public void A_glob_in_nfd_matches_a_file_stored_in_nfc()
    {
        WriteCafe();

        Assert.Equal([$"wiki/people/{Nfc}.md"], Paths("--glob", $"wiki/people/{Nfd}.md"));
        Assert.Equal(["wiki/notes/a.md"], Paths("--glob", "wiki/**", "--glob", $"!wiki/people/{Nfd}.md"));
    }

    [Fact]
    public void A_from_glob_in_nfd_matches_a_source_stored_in_nfc()
    {
        _workspace.Write($"{Nfc}.md", "[x](x.md)\n");
        _workspace.Write("other.md", "[x](x.md)\n");
        _workspace.Write("x.md", "# X\n");

        var backrefs = Json(_workspace.Run("backrefs", "x.md", "--from", $"{Nfd}.md", "--json"));

        Assert.Equal($"{Nfc}.md", Assert.Single(backrefs.EnumerateArray()).GetProperty("source").GetString());
        Assert.DoesNotContain("x.md", Paths("--no-backrefs", "--from", $"{Nfd}.md"));
    }

    [Fact]
    public void A_glob_in_nfc_matches_a_file_stored_in_nfd()
    {
        _workspace.Write($"wiki/people/{Nfd}.md", "# Café\n");
        _workspace.Write("wiki/notes/a.md", "# A\n");

        Assert.Equal([$"wiki/people/{Nfd}.md"], Paths("--glob", $"wiki/people/{Nfc}.md"));
        Assert.Equal(["wiki/notes/a.md"], Paths("--glob", "wiki/**", "--glob", $"!wiki/people/{Nfc}.md"));
    }

    [Fact]
    public void A_from_glob_in_nfc_matches_a_source_stored_in_nfd()
    {
        _workspace.Write($"{Nfd}.md", "[x](x.md)\n");
        _workspace.Write("other.md", "[x](x.md)\n");
        _workspace.Write("x.md", "# X\n");

        var backrefs = Json(_workspace.Run("backrefs", "x.md", "--from", $"{Nfc}.md", "--json"));

        Assert.Equal($"{Nfd}.md", Assert.Single(backrefs.EnumerateArray()).GetProperty("source").GetString());
    }

    [Fact]
    public void A_link_in_nfd_to_a_file_stored_in_nfc_is_not_broken()
    {
        WriteCafe();

        var result = _workspace.Run("lint", "--rule", "broken-link");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void A_link_in_nfd_counts_as_a_backref_and_a_ref()
    {
        _workspace.Write($"wiki/people/{Nfc}.md", "# Café\n");
        _workspace.Write("wiki/notes/a.md", $"[a](../people/{Nfd}.md)\n");

        Assert.DoesNotContain($"wiki/people/{Nfc}.md", Paths("--no-backrefs"));
        Assert.DoesNotContain("wiki/notes/a.md", Paths("--no-refs"));
    }

    [Fact]
    public void An_okf_index_entry_in_nfd_lists_a_page_stored_in_nfc()
    {
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb"] }""");
        _workspace.Write("kb/index.md", $"---\nokf_version: \"0.2\"\n---\n# KB\n\n* [Café]({Nfd}.md) - A place\n");
        _workspace.Write($"kb/{Nfc}.md", "---\ntype: Place\ndescription: A place\n---\n");

        var result = _workspace.Run("lint", "--rule", "okf-index");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void An_okf_index_entry_in_nfc_lists_a_page_stored_in_nfd()
    {
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb"] }""");
        _workspace.Write("kb/index.md", $"---\nokf_version: \"0.2\"\n---\n# KB\n\n* [Café]({Nfc}.md) - A place\n");
        _workspace.Write($"kb/{Nfd}.md", "---\ntype: Place\ndescription: A place\n---\n");

        var result = _workspace.Run("lint", "--rule", "okf-index");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void An_okf_index_entry_in_either_form_is_compared_with_its_pages_description()
    {
        // The page is found, so a description that differs from it is reported, whichever form each is in.
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb"] }""");
        _workspace.Write("kb/index.md", $"---\nokf_version: \"0.2\"\n---\n# KB\n\n* [Café]({Nfd}.md) - Not a place\n");
        _workspace.Write($"kb/{Nfc}.md", "---\ntype: Place\ndescription: A place\n---\n");

        var result = _workspace.Run("lint", "--rule", "okf-index");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match the page's description 'A place'", result.Stdout);
    }

    [Fact]
    public void An_index_from_before_paths_were_matched_under_nfd_matches_them_after_migrating()
    {
        WriteCafe();
        _workspace.Settle();
        _workspace.RollBackIndexTo(5);

        var json = Json(_workspace.Run("refs", "wiki/notes/a.md", "--json"));

        Assert.Equal(["file", "file"], json.EnumerateArray().Select(l => l.GetProperty("type").GetString()));
    }
}
