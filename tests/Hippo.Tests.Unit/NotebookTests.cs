using Hippo.Notebooks;

namespace Hippo.Tests.Unit;

public class NotebookTests
{
    [Fact]
    public void The_root_is_the_nearest_folder_with_a_config()
    {
        using var dir = new TempDirectory();
        dir.Write("notes/.hippo/config.json", "");
        Directory.CreateDirectory(dir.Combine("notes/wiki/topics"));

        var notebook = Notebook.Open(dir.Combine("notes/wiki/topics"));

        Assert.Equal(dir.Combine("notes"), notebook.Root);
    }

    [Fact]
    public void The_working_directory_itself_can_be_the_root()
    {
        using var dir = new TempDirectory();
        dir.Write(".hippo/config.json", "");

        var notebook = Notebook.Open(dir.FullPath);

        Assert.Equal(dir.FullPath, notebook.Root);
    }

    [Fact]
    public void No_config_anywhere_up_the_tree_is_an_error()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("a/b"));

        var ex = Assert.Throws<HippoException>(() => Notebook.Open(dir.Combine("a/b")));

        Assert.Contains(".hippo/config.json", ex.Message);
    }

    [Fact]
    public void A_hippo_folder_without_a_config_is_not_a_notebook()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine(".hippo"));

        Assert.Null(Notebook.FindRoot(dir.FullPath));
    }

    [Fact]
    public void Find_root_is_null_when_no_folder_up_the_tree_has_a_config()
    {
        using var dir = new TempDirectory();

        Assert.Null(Notebook.FindRoot(dir.FullPath));
    }

    [Fact]
    public void The_starter_config_parses()
    {
        var config = NotebookConfig.Parse(NotebookConfig.Starter);

        Assert.Equal(["**/*"], config.Include);
        Assert.Equal([".git/**", ".obsidian/**", ".trash/**"], config.Exclude);
        Assert.Equal(NotebookConfig.Default.LinkSettings.Fingerprint, config.LinkSettings.Fingerprint);
    }

    [Fact]
    public void The_starter_configs_commented_examples_parse()
    {
        var uncommented = System.Text.RegularExpressions.Regex.Replace(NotebookConfig.Starter, "^( *)// ", "$1",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            // The example's page is the default, so it is swapped for bundle to show the line was actually read.
            .Replace("\"body\": { \"resolve\": \"page\" }", "\"body\": { \"resolve\": \"bundle\" }");

        Assert.DoesNotContain("\"page\"", uncommented);

        var config = NotebookConfig.Parse(uncommented);

        // Every commented key is checked, since a misspelt one would be ignored rather than rejected.
        Assert.Equal(["wiki"], config.Bundles);
        Assert.Equal(LinkBase.Bundle, config.Links.Body);
        Assert.Equal([("sources[].resource", LinkBase.Bundle)], config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
        Assert.Equal(["wiki/index.md"], config.Links.Roots);
    }

    [Fact]
    public void Files_section_sets_include_and_exclude()
    {
        var config = NotebookConfig.Parse("""
            { "files": { "include": ["**/*.md", "raw/**"], "exclude": [".git/**", "inbox/**"] } }
            """);

        Assert.Equal(["**/*.md", "raw/**"], config.Include);
        Assert.Equal([".git/**", "inbox/**"], config.Exclude);
    }

    [Fact]
    public void Without_a_files_section_everything_is_included()
    {
        var config = NotebookConfig.Parse("""{ "links": { "roots": ["index.md"] } }""");

        Assert.Equal(["**/*"], config.Include);
        Assert.Empty(config.Exclude);
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
        var config = NotebookConfig.Parse(json);

        Assert.Equal(["**/*"], config.Include);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var config = NotebookConfig.Parse("""
            {
              // line comment
              "files": { "include": ["**/*.md",], /* block comment */ },
            }
            """);

        Assert.Equal(["**/*.md"], config.Include);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var config = NotebookConfig.Parse("""
            {
              "files": { "include": ["**/*.md"], "typo": true },
              "ids": { "field": "sources[].id" },
              "links": { "typo": 1, "body": { "resolve": "bundle", "typo": 2 } }
            }
            """);

        Assert.Equal(["**/*.md"], config.Include);
        Assert.Equal(LinkBase.Bundle, config.Links.Body);
    }

    [Fact]
    public void Bundles_and_links_are_read()
    {
        var config = NotebookConfig.Parse("""
            {
              "bundles": [{ "root": "wiki" }, { "root": "/docs/" }],
              "links": {
                "body": { "resolve": "bundle" },
                "wikilinks": "text",
                "frontmatter": [
                  { "field": "sources[].resource", "resolve": "bundle" },
                  { "field": "related[]" }
                ],
                "roots": ["wiki/index.md"]
              }
            }
            """);

        Assert.Equal(["wiki", "docs"], config.Bundles);
        Assert.Equal(LinkBase.Bundle, config.Links.Body);
        Assert.Equal(
            [("sources[].resource", LinkBase.Bundle), ("related[]", LinkBase.Page)],
            config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
        Assert.Equal(["wiki/index.md"], config.Links.Roots);
    }

    [Fact]
    public void Without_a_links_section_body_links_resolve_from_the_page()
    {
        var config = NotebookConfig.Parse("""{ "files": { "include": ["**/*"] } }""");

        Assert.Empty(config.Bundles);
        Assert.Equal(LinkBase.Page, config.Links.Body);
        Assert.Empty(config.Links.Frontmatter);
        Assert.Empty(config.Links.Roots);
    }

    [Theory]
    [InlineData("""{ "bundles": "wiki" }""", "bundles")]
    [InlineData("""{ "bundles": [{ "root": "../outside" }] }""", "bundles")]
    [InlineData("""{ "bundles": [{}] }""", "bundles")]
    [InlineData("""{ "bundles": [{ "root": 1 }] }""", "bundles")]
    [InlineData("""{ "links": { "body": { "resolve": "folder" } } }""", "links.body.resolve")]
    [InlineData("""{ "links": { "wikilinks": "resolve" } }""", "links.wikilinks")]
    [InlineData("""{ "links": { "frontmatter": [{ "resolve": "page" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a..b" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a[]b" }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "roots": "wiki/index.md" } }""", "links.roots")]
    public void Malformed_link_settings_are_errors(string json, string key)
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(json));

        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("""{ "files": { "include": ["**/*.md", 1] } }""", "files.include")]
    [InlineData("""{ "files": { "exclude": [true] } }""", "files.exclude")]
    [InlineData("""{ "links": { "roots": [null] } }""", "links.roots")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": 1 }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "body": { "resolve": true } } }""", "links.body.resolve")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "resolve": null }] } }""", "links.frontmatter[].resolve")]
    public void Values_that_are_not_strings_are_errors(string json, string key)
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(json));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_settings_that_shape_links_only()
    {
        string Fingerprint(string json) => NotebookConfig.Parse(json).LinkSettings.Fingerprint;
        var baseline = Fingerprint("""{ "bundles": [{ "root": "wiki" }], "links": { "frontmatter": [{ "field": "a" }] } }""");

        Assert.Equal(baseline, Fingerprint("""{ "bundles": [{ "root": "wiki" }], "links": { "frontmatter": [{ "field": "a" }], "roots": ["x.md"] }, "files": { "exclude": ["y/**"] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": [{ "root": "docs" }], "links": { "frontmatter": [{ "field": "a" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": [{ "root": "wiki" }], "links": { "frontmatter": [{ "field": "a", "resolve": "bundle" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": [{ "root": "wiki" }], "links": { "body": { "resolve": "bundle" }, "frontmatter": [{ "field": "a" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": [{ "root": "wiki" }], "links": { "frontmatter": [{ "field": "b" }] } }"""));
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("{\n  \"files\": [unclosed\n"));

        Assert.Contains(".hippo/config.json: line 2: invalid JSON", ex.Message);
    }

    [Fact]
    public void A_duplicate_key_is_an_error_that_names_the_key()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("""{ "files": {}, "files": {} }"""));

        Assert.Contains("invalid JSON", ex.Message);
        Assert.Contains("'files'", ex.Message);
        Assert.DoesNotContain("line :", ex.Message);
    }

    [Fact]
    public void An_unclosed_comment_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("/* unclosed"));

        Assert.Contains("invalid JSON", ex.Message);
    }

    [Theory]
    [InlineData("""{ "\ud800": 1 }""")]
    [InlineData("""{ "files": { "include": ["\ud800"] } }""")]
    public void An_escaped_lone_surrogate_is_a_config_error_not_a_crash(string json)
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(json));

        Assert.Contains(".hippo/config.json: invalid JSON", ex.Message);
    }

    [Fact]
    public void Deep_nesting_is_an_error_not_a_crash()
    {
        var json = $"{{ \"a\": {new string('[', 100_000)}{new string(']', 100_000)} }}";

        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(json));

        Assert.Contains(".hippo/config.json", ex.Message);
    }

    [Fact]
    public void An_include_that_is_not_a_list_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("""{ "files": { "include": "**/*" } }"""));

        Assert.Contains("files.include", ex.Message);
    }

    [Fact]
    public void A_config_that_is_not_an_object_is_an_error()
    {
        Assert.Throws<HippoException>(() => NotebookConfig.Parse("""["a", "b"]"""));
    }

    [Theory]
    [InlineData("notes/a.md", true)]
    [InlineData("a.md", true)]
    [InlineData("a.markdown", false)]
    [InlineData("a.md.txt", false)]
    [InlineData("image.png", false)]
    public void Only_names_ending_in_md_are_markdown(string path, bool expected)
    {
        Assert.Equal(expected, Notebook.IsMarkdown(path));
    }
}
