using Hippo.Notebooks;

namespace Hippo.Tests.Unit;

public class NotebookTests
{
    [Fact]
    public void The_root_is_the_nearest_folder_with_a_config()
    {
        using var dir = new TempDirectory();
        dir.Write("notes/.hippo.yaml", "version: 1\n");
        Directory.CreateDirectory(dir.Combine("notes/wiki/topics"));

        var notebook = Notebook.Open(dir.Combine("notes/wiki/topics"));

        Assert.Equal(dir.Combine("notes"), notebook.Root);
    }

    [Fact]
    public void The_working_directory_itself_can_be_the_root()
    {
        using var dir = new TempDirectory();
        dir.Write(".hippo.yaml", "version: 1\n");

        var notebook = Notebook.Open(dir.FullPath);

        Assert.Equal(dir.FullPath, notebook.Root);
    }

    [Fact]
    public void No_config_anywhere_up_the_tree_is_an_error()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("a/b"));

        var ex = Assert.Throws<HippoException>(() => Notebook.Open(dir.Combine("a/b")));

        Assert.Contains(".hippo.yaml", ex.Message);
    }

    [Fact]
    public void Files_section_sets_include_and_exclude()
    {
        var config = NotebookConfig.Parse("""
            version: 1
            files:
              include: ["**/*.md", "raw/**"]
              exclude: [".git/**", "inbox/**"]
            """, []);

        Assert.Equal(["**/*.md", "raw/**"], config.Include);
        Assert.Equal([".git/**", "inbox/**"], config.Exclude);
    }

    [Fact]
    public void Without_a_files_section_everything_is_included()
    {
        var config = NotebookConfig.Parse("version: 1\n", []);

        Assert.Equal(["**/*"], config.Include);
        Assert.Empty(config.Exclude);
    }

    [Fact]
    public void An_empty_config_is_valid()
    {
        var config = NotebookConfig.Parse("", []);

        Assert.Equal(["**/*"], config.Include);
    }

    [Fact]
    public void Keys_from_later_phases_are_ignored_with_a_warning()
    {
        var warnings = new List<string>();

        var config = NotebookConfig.Parse("""
            version: 1
            files:
              include: ["**/*"]
              typo: true
            ids:
              field: sources[].id
            lint:
              rules: [placement]
            """, warnings);

        Assert.Equal(["**/*"], config.Include);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'ids'"));
        Assert.Contains(warnings, w => w.Contains("'lint'"));
        Assert.Contains(warnings, w => w.Contains("'files.typo'"));
    }

    [Fact]
    public void Bundles_and_links_are_read_without_warnings()
    {
        var warnings = new List<string>();

        var config = NotebookConfig.Parse("""
            version: 1
            bundles:
              - root: wiki
              - root: /docs/
            links:
              body: { resolve: bundle }
              wikilinks: text
              frontmatter:
                - field: sources[].resource
                  resolve: bundle
                - field: related[]
              roots: ["wiki/index.md"]
            """, warnings);

        Assert.Empty(warnings);
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
        var config = NotebookConfig.Parse("version: 1\n", []);

        Assert.Empty(config.Bundles);
        Assert.Equal(LinkBase.Page, config.Links.Body);
        Assert.Empty(config.Links.Frontmatter);
        Assert.Empty(config.Links.Roots);
    }

    [Theory]
    [InlineData("bundles: wiki\n", "bundles")]
    [InlineData("bundles:\n  - root: ../outside\n", "bundles")]
    [InlineData("bundles:\n  - {}\n", "bundles")]
    [InlineData("links:\n  body: { resolve: folder }\n", "links.body.resolve")]
    [InlineData("links:\n  wikilinks: resolve\n", "links.wikilinks")]
    [InlineData("links:\n  frontmatter:\n    - resolve: page\n", "links.frontmatter")]
    [InlineData("links:\n  frontmatter:\n    - field: a..b\n", "links.frontmatter")]
    [InlineData("links:\n  frontmatter:\n    - field: a[]b\n", "links.frontmatter")]
    [InlineData("links:\n  roots: wiki/index.md\n", "links.roots")]
    public void Malformed_link_settings_are_errors(string yaml, string key)
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(yaml, []));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Unknown_keys_inside_links_warn()
    {
        var warnings = new List<string>();

        NotebookConfig.Parse("links:\n  typo: 1\n  body: { typo: 2 }\n", warnings);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'links.typo'"));
        Assert.Contains(warnings, w => w.Contains("'links.body.typo'"));
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_settings_that_shape_links_only()
    {
        string Fingerprint(string yaml) => NotebookConfig.Parse(yaml, []).LinkSettings.Fingerprint;
        var baseline = Fingerprint("bundles: [{root: wiki}]\nlinks:\n  frontmatter: [{field: a}]\n");

        Assert.Equal(baseline, Fingerprint("bundles: [{root: wiki}]\nlinks:\n  frontmatter: [{field: a}]\n  roots: [x.md]\nfiles:\n  exclude: [y/**]\n"));
        Assert.NotEqual(baseline, Fingerprint("bundles: [{root: docs}]\nlinks:\n  frontmatter: [{field: a}]\n"));
        Assert.NotEqual(baseline, Fingerprint("bundles: [{root: wiki}]\nlinks:\n  frontmatter: [{field: a, resolve: bundle}]\n"));
        Assert.NotEqual(baseline, Fingerprint("bundles: [{root: wiki}]\nlinks:\n  body: {resolve: bundle}\n  frontmatter: [{field: a}]\n"));
        Assert.NotEqual(baseline, Fingerprint("bundles: [{root: wiki}]\nlinks:\n  frontmatter: [{field: b}]\n"));
    }

    [Fact]
    public void Invalid_yaml_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("files: [unclosed\n", []));

        Assert.Contains(".hippo.yaml", ex.Message);
    }

    [Fact]
    public void Deep_nesting_is_an_error_not_a_crash()
    {
        var yaml = $"a: {new string('[', 100_000)}{new string(']', 100_000)}\n";

        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(yaml, []));

        Assert.Contains(".hippo.yaml", ex.Message);
    }

    [Fact]
    public void A_version_other_than_1_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("version: 2\n", []));

        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public void An_include_that_is_not_a_list_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("files:\n  include: \"**/*\"\n", []));

        Assert.Contains("files.include", ex.Message);
    }

    [Fact]
    public void A_config_that_is_not_a_mapping_is_an_error()
    {
        Assert.Throws<HippoException>(() => NotebookConfig.Parse("- a\n- b\n", []));
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
