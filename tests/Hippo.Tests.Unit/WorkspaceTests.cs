using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

public class WorkspaceTests
{
    [Fact]
    public void The_root_is_the_nearest_folder_with_a_config()
    {
        using var dir = new TempDirectory();
        dir.Write("notes/.hippo/config.json", "");
        Directory.CreateDirectory(dir.Combine("notes/wiki/topics"));

        var workspace = Workspace.Open(dir.Combine("notes/wiki/topics"));

        Assert.Equal(dir.Combine("notes"), workspace.Root);
    }

    [Fact]
    public void The_working_directory_itself_can_be_the_root()
    {
        using var dir = new TempDirectory();
        dir.Write(".hippo/config.json", "");

        var workspace = Workspace.Open(dir.FullPath);

        Assert.Equal(dir.FullPath, workspace.Root);
    }

    [Fact]
    public void No_config_anywhere_up_the_tree_is_an_error()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("a/b"));

        var ex = Assert.Throws<HippoException>(() => Workspace.Open(dir.Combine("a/b")));

        Assert.Contains(".hippo/config.json", ex.Message);
    }

    [Fact]
    public void A_hippo_folder_without_a_config_is_not_a_workspace()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine(".hippo"));

        Assert.Null(Workspace.FindRoot(dir.FullPath));
    }

    [Fact]
    public void Find_root_is_null_when_no_folder_up_the_tree_has_a_config()
    {
        using var dir = new TempDirectory();

        Assert.Null(Workspace.FindRoot(dir.FullPath));
    }

    [Fact]
    public void The_starter_config_parses()
    {
        var config = WorkspaceConfig.Parse(WorkspaceConfig.Starter);

        Assert.Equal(["**/*"], config.Include);
        Assert.Equal([".git/**", ".obsidian/**", ".trash/**"], config.Exclude);
        Assert.True(config.Gitignore);
        Assert.Equal(WorkspaceConfig.Default.Links.Fingerprint, config.Links.Fingerprint);
    }

    [Fact]
    public void The_starter_configs_commented_examples_parse()
    {
        var uncommented = System.Text.RegularExpressions.Regex.Replace(WorkspaceConfig.Starter, "^( *)// ", "$1",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var config = WorkspaceConfig.Parse(uncommented);

        Assert.Equal(["wiki"], config.Links.Bundles);
        Assert.Equal([("sources[].resource", LinkBase.Bundle)], config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
    }

    [Fact]
    public void Files_section_sets_include_and_exclude()
    {
        var config = WorkspaceConfig.Parse("""
            { "files": { "include": ["**/*.md", "raw/**"], "exclude": [".git/**", "inbox/**"] } }
            """);

        Assert.Equal(["**/*.md", "raw/**"], config.Include);
        Assert.Equal([".git/**", "inbox/**"], config.Exclude);
    }

    [Fact]
    public void Without_a_files_section_everything_is_included()
    {
        var config = WorkspaceConfig.Parse("""{ "links": { "bundles": ["wiki"] } }""");

        Assert.Equal(["**/*"], config.Include);
        Assert.Empty(config.Exclude);
        Assert.True(config.Gitignore);
    }

    [Fact]
    public void Gitignore_can_be_turned_off()
    {
        var config = WorkspaceConfig.Parse("""{ "files": { "gitignore": false } }""");

        Assert.False(config.Gitignore);
        Assert.Equal(["**/*"], config.Include);
    }

    [Theory]
    [InlineData("""{ "files": { "gitignore": "false" } }""")]
    [InlineData("""{ "files": { "gitignore": 0 } }""")]
    [InlineData("""{ "files": { "gitignore": null } }""")]
    public void Gitignore_must_be_a_boolean(string json)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Equal(".hippo/config.json: files.gitignore must be true or false", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n")]
    [InlineData("{}")]
    [InlineData("// nothing configured yet\n")]
    [InlineData("// no trailing newline")]
    [InlineData("/* block */ \n")]
    public void An_empty_config_is_valid(string json)
    {
        var config = WorkspaceConfig.Parse(json);

        Assert.Equal(["**/*"], config.Include);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var config = WorkspaceConfig.Parse("""
            {
              // line comment
              "files": { "include": ["**/*.md",], /* block comment */ },
            }
            """);

        Assert.Equal(["**/*.md"], config.Include);
    }

    [Theory]
    [InlineData("""{ "fils": {} }""", "unknown key fils; expected files or links")]
    [InlineData("""{ "files": { "exlcude": [] } }""", "unknown key files.exlcude; expected include or exclude or gitignore")]
    [InlineData("""{ "links": { "roots": [] } }""", "unknown key links.roots; expected bundles or frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "reslove": "page" }] } }""", "unknown key links.frontmatter[].reslove")]
    public void Unknown_keys_are_errors_that_name_the_key(string json, string message)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Links_are_read()
    {
        var config = WorkspaceConfig.Parse("""
            {
              "links": {
                "bundles": ["wiki", "/docs/"],
                "frontmatter": [
                  { "field": "sources[].resource", "resolve": "bundle" },
                  { "field": "related[]" }
                ]
              }
            }
            """);

        Assert.Equal(["wiki", "docs"], config.Links.Bundles);
        Assert.Equal(
            [("sources[].resource", LinkBase.Bundle), ("related[]", LinkBase.Page)],
            config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
    }

    [Fact]
    public void Without_a_links_section_there_are_no_bundles_or_frontmatter_links()
    {
        var config = WorkspaceConfig.Parse("""{ "files": { "include": ["**/*"] } }""");

        Assert.Empty(config.Links.Bundles);
        Assert.Empty(config.Links.Frontmatter);
    }

    [Theory]
    [InlineData("""{ "links": { "bundles": "wiki" } }""", "links.bundles")]
    [InlineData("""{ "links": { "bundles": ["../outside"] } }""", "links.bundles")]
    [InlineData("""{ "links": { "bundles": ["/"] } }""", "links.bundles")]
    [InlineData("""{ "links": { "bundles": [{ "root": "wiki" }] } }""", "links.bundles")]
    [InlineData("""{ "links": { "frontmatter": [{ "resolve": "page" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a..b" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a[]b" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "resolve": "folder" }] } }""", "links.frontmatter[].resolve")]
    public void Malformed_link_settings_are_errors(string json, string key)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("""{ "files": { "include": ["**/*.md", 1] } }""", "files.include")]
    [InlineData("""{ "files": { "exclude": [true] } }""", "files.exclude")]
    [InlineData("""{ "links": { "bundles": [null] } }""", "links.bundles")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": 1 }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "resolve": null }] } }""", "links.frontmatter[].resolve")]
    public void Values_that_are_not_strings_are_errors(string json, string key)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_settings_that_shape_links_only()
    {
        string Fingerprint(string json) => WorkspaceConfig.Parse(json).Links.Fingerprint;
        var baseline = Fingerprint("""{ "links": { "bundles": ["wiki"], "frontmatter": [{ "field": "a" }] } }""");

        Assert.Equal(baseline, Fingerprint("""{ "links": { "bundles": ["wiki"], "frontmatter": [{ "field": "a" }] }, "files": { "exclude": ["y/**"] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "links": { "bundles": ["docs"], "frontmatter": [{ "field": "a" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "links": { "bundles": ["wiki"], "frontmatter": [{ "field": "a", "resolve": "bundle" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "links": { "bundles": ["wiki"], "frontmatter": [{ "field": "b" }] } }"""));
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse("{\n  \"files\": [unclosed\n"));

        Assert.Contains(".hippo/config.json: line 2: invalid JSON", ex.Message);
    }

    [Fact]
    public void A_duplicate_key_is_an_error_that_names_the_key()
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse("""{ "files": {}, "files": {} }"""));

        Assert.Contains("invalid JSON", ex.Message);
        Assert.Contains("'files'", ex.Message);
        Assert.DoesNotContain("line :", ex.Message);
    }

    [Fact]
    public void An_unclosed_comment_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse("/* unclosed"));

        Assert.Contains("invalid JSON", ex.Message);
    }

    [Theory]
    [InlineData("""{ "\ud800": 1 }""")]
    [InlineData("""{ "files": { "include": ["\ud800"] } }""")]
    public void An_escaped_lone_surrogate_is_a_config_error_not_a_crash(string json)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(".hippo/config.json: invalid JSON", ex.Message);
    }

    [Fact]
    public void Deep_nesting_is_an_error_not_a_crash()
    {
        var json = $"{{ \"a\": {new string('[', 100_000)}{new string(']', 100_000)} }}";

        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(".hippo/config.json", ex.Message);
    }

    [Fact]
    public void An_include_that_is_not_a_list_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse("""{ "files": { "include": "**/*" } }"""));

        Assert.Contains("files.include", ex.Message);
    }

    [Fact]
    public void A_config_that_is_not_an_object_is_an_error()
    {
        Assert.Throws<HippoException>(() => WorkspaceConfig.Parse("""["a", "b"]"""));
    }

    [Theory]
    [InlineData("notes/a.md", true)]
    [InlineData("a.md", true)]
    [InlineData("a.markdown", false)]
    [InlineData("a.md.txt", false)]
    [InlineData("image.png", false)]
    public void Only_names_ending_in_md_are_markdown(string path, bool expected)
    {
        Assert.Equal(expected, Workspace.IsMarkdown(path));
    }
}
