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
        Assert.Equal(new PageSettings(WorkspaceConfig.Default.Bundles, WorkspaceConfig.Default.Links, []).Fingerprint,
            new PageSettings(config.Bundles, config.Links, []).Fingerprint);
        Assert.Empty(config.LintOff);
        Assert.Equal(SearchTokenizer.Porter, config.SearchTokenizer);
    }

    [Fact]
    public void The_starter_configs_commented_examples_parse()
    {
        var uncommented = System.Text.RegularExpressions.Regex.Replace(WorkspaceConfig.Starter, "^( *)// ", "$1",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var config = WorkspaceConfig.Parse(uncommented);

        Assert.Equal(["wiki"], config.Bundles);
        Assert.Equal([("related[]", LinkBase.Page)], config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
        Assert.Equal(["okf-footnote @ everywhere", "okf-index @ wiki/drafts/**", "every rule @ archive/**"], Off(config));
        Assert.Equal(SearchTokenizer.Trigram, config.SearchTokenizer);
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
        var config = WorkspaceConfig.Parse("""{ "bundles": ["wiki"] }""");

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
    [InlineData("""{ "fils": {} }""", "unknown key fils; expected files or bundles or links or lint or search")]
    [InlineData("""{ "search": { "tokeniser": "porter" } }""", "unknown key search.tokeniser; expected tokenizer")]
    [InlineData("""{ "lint": { "of": [] } }""", "unknown key lint.of; expected off")]
    [InlineData("""{ "lint": { "exclude": ["archive/**"] } }""", "unknown key lint.exclude; expected off")]
    [InlineData("""{ "lint": { "off": [{ "path": ["archive/**"] }] } }""", "unknown key lint.off[].path; expected rules or paths")]
    [InlineData("""{ "files": { "exlcude": [] } }""", "unknown key files.exlcude; expected include or exclude or gitignore")]
    [InlineData("""{ "links": { "roots": [] } }""", "unknown key links.roots; expected frontmatter")]
    [InlineData("""{ "links": { "bundles": ["wiki"] } }""", "unknown key links.bundles; expected frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "reslove": "page" }] } }""", "unknown key links.frontmatter[].reslove")]
    public void Unknown_keys_are_errors_that_name_the_key(string json, string message)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Bundles_and_links_are_read()
    {
        var config = WorkspaceConfig.Parse("""
            {
              "bundles": ["wiki", "/docs/"],
              "links": {
                "frontmatter": [
                  { "field": "sources[].resource", "resolve": "bundle" },
                  { "field": "related[]" }
                ]
              }
            }
            """);

        Assert.Equal(["wiki", "docs"], config.Bundles);
        Assert.Equal(
            [("sources[].resource", LinkBase.Bundle), ("related[]", LinkBase.Page)],
            config.Links.Frontmatter.Select(f => (f.Field, f.Resolve)));
    }

    [Fact]
    public void Without_bundles_or_a_links_section_there_are_no_bundles_or_frontmatter_links()
    {
        var config = WorkspaceConfig.Parse("""{ "files": { "include": ["**/*"] } }""");

        Assert.Empty(config.Bundles);
        Assert.Empty(config.Links.Frontmatter);
    }

    [Theory]
    [InlineData("""{ "bundles": "wiki" }""", "bundles must be an array of folders inside the workspace")]
    [InlineData("""{ "bundles": ["../outside"] }""", "bundles must be an array of folders inside the workspace")]
    [InlineData("""{ "bundles": ["wiki/./sub"] }""", "bundles must be an array of folders inside the workspace")]
    [InlineData("""{ "bundles": ["/"] }""", "bundles must be an array of folders inside the workspace")]
    [InlineData("""{ "bundles": [{ "root": "wiki" }] }""", "bundles must be an array of folders inside the workspace")]
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
    [InlineData("""{ "bundles": [null] }""", "bundles must be an array of folders inside the workspace")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": 1 }] } }""", "links.frontmatter")]
    [InlineData("""{ "links": { "frontmatter": [{ "field": "a", "resolve": null }] } }""", "links.frontmatter[].resolve")]
    public void Values_that_are_not_strings_are_errors(string json, string key)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(key, ex.Message);
    }

    /// <summary>Each <c>lint.off</c> entry as <c>rules @ paths</c>.</summary>
    private static List<string> Off(WorkspaceConfig config) => config.LintOff
        .Select(o => $"{(o.Rules is null ? "every rule" : string.Join(" ", o.Rules))} @ {(o.Paths is null ? "everywhere" : string.Join(" ", o.Paths))}")
        .ToList();

    [Fact]
    public void Lint_off_names_rules_okf_must_rules_included()
    {
        var config = WorkspaceConfig.Parse("""{ "lint": { "off": ["okf-type", "okf-footnote", "broken-link"] } }""");

        Assert.Equal(["okf-type @ everywhere", "okf-footnote @ everywhere", "broken-link @ everywhere"], Off(config));
    }

    [Fact]
    public void A_star_in_a_rule_name_matches_any_run_of_characters()
    {
        var config = WorkspaceConfig.Parse("""{ "lint": { "off": ["okf-index*", { "rules": ["*-link", "okf-t*"], "paths": ["kb/**"] }] } }""");

        Assert.Equal(["okf-index-frontmatter okf-index @ everywhere", "broken-link okf-type okf-timestamp @ kb/**"], Off(config));
    }

    [Fact]
    public void An_entry_with_paths_turns_off_its_rules_or_every_rule_on_the_files_they_match()
    {
        var config = WorkspaceConfig.Parse("""
            { "lint": { "off": [{ "rules": ["okf-index"], "paths": ["kb/drafts/**", "kb/scratch.md"] }, { "paths": ["archive/**"] }] } }
            """);

        Assert.Equal(["okf-index @ kb/drafts/** kb/scratch.md", "every rule @ archive/**"], Off(config));
    }

    [Theory]
    [InlineData("""{ "lint": { "off": ["okf-footnotes"] } }""", "lint.off: no rule matches okf-footnotes; expected one of okf-type, ")]
    [InlineData("""{ "lint": { "off": ["okf_*"] } }""", "lint.off: no rule matches okf_*; expected one of okf-type, ")]
    [InlineData("""{ "lint": { "off": [{ "rules": ["okf-nope"], "paths": ["kb/**"] }] } }""", "lint.off[].rules: no rule matches okf-nope")]
    [InlineData("""{ "lint": { "off": "okf-footnote" } }""", "lint.off must be an array of rule names and objects with paths")]
    [InlineData("""{ "lint": { "off": [1] } }""", "lint.off must be an array of rule names and objects with paths")]
    [InlineData("""{ "lint": { "off": [{ "rules": ["okf-index"] }] } }""", "lint.off[].paths must be an array of glob patterns")]
    [InlineData("""{ "lint": { "off": [{ "rules": ["okf-index"], "paths": [] }] } }""", "lint.off[].paths must be an array of glob patterns")]
    [InlineData("""{ "lint": { "off": [{ "paths": [""] }] } }""", "lint.off[].paths must be an array of glob patterns")]
    [InlineData("""{ "lint": { "off": [{ "rules": [], "paths": ["kb/**"] }] } }""", "lint.off[].rules must be an array of rule names")]
    [InlineData("""{ "lint": { "off": [{ "rules": "okf-index", "paths": ["kb/**"] }] } }""", "lint.off[].rules must be an array of rule names")]
    [InlineData("""{ "lint": [] }""", "lint must be an object")]
    public void Lint_off_refuses_unknown_rules_and_malformed_entries(string json, string message)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_okf_bundles()
    {
        string Fingerprint(params string[] okf) => new PageSettings(["wiki", "docs"], LinkSettings.Default, okf).Fingerprint;

        Assert.NotEqual(Fingerprint(), Fingerprint("wiki"));
        Assert.NotEqual(Fingerprint("docs"), Fingerprint("wiki"));
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_settings_that_shape_links_only()
    {
        string Fingerprint(string json)
        {
            var config = WorkspaceConfig.Parse(json);
            return new PageSettings(config.Bundles, config.Links, []).Fingerprint;
        }
        var baseline = Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "a" }] } }""");

        Assert.Equal(baseline, Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "a" }] }, "files": { "exclude": ["y/**"] } }"""));
        Assert.Equal(baseline, Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "a" }] }, "lint": { "off": ["okf-status"] } }"""));
        Assert.Equal(baseline, Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "a" }] }, "lint": { "off": [{ "paths": ["raw/**"] }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": ["docs"], "links": { "frontmatter": [{ "field": "a" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "a", "resolve": "bundle" }] } }"""));
        Assert.NotEqual(baseline, Fingerprint("""{ "bundles": ["wiki"], "links": { "frontmatter": [{ "field": "b" }] } }"""));
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
    [InlineData("""{ "search": { "tokenizer": "porter" } }""", false)]
    [InlineData("""{ "search": { "tokenizer": "trigram" } }""", true)]
    [InlineData("""{ "search": {} }""", false)]
    [InlineData("""{}""", false)]
    public void Search_tokenizer_is_porter_unless_set_to_trigram(string json, bool trigram)
    {
        Assert.Equal(trigram ? SearchTokenizer.Trigram : SearchTokenizer.Porter, WorkspaceConfig.Parse(json).SearchTokenizer);
    }

    [Theory]
    [InlineData("""{ "search": { "tokenizer": "unicode61" } }""")]
    [InlineData("""{ "search": { "tokenizer": "porter unicode61" } }""")]
    [InlineData("""{ "search": { "tokenizer": 1 } }""")]
    [InlineData("""{ "search": [] }""")]
    public void A_search_tokenizer_other_than_porter_or_trigram_is_an_error(string json)
    {
        var ex = Assert.Throws<HippoException>(() => WorkspaceConfig.Parse(json));

        Assert.Contains("search", ex.Message);
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
