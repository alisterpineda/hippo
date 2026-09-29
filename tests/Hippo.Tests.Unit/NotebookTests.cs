using Hippo.Notebooks;

namespace Hippo.Tests.Unit;

public class NotebookTests
{
    [Fact]
    public void The_root_is_the_nearest_folder_with_a_config()
    {
        using var dir = new TempDirectory();
        dir.Write("notes/.hippo.yaml", "");
        Directory.CreateDirectory(dir.Combine("notes/wiki/topics"));

        var notebook = Notebook.Open(dir.Combine("notes/wiki/topics"));

        Assert.Equal(dir.Combine("notes"), notebook.Root);
    }

    [Fact]
    public void The_working_directory_itself_can_be_the_root()
    {
        using var dir = new TempDirectory();
        dir.Write(".hippo.yaml", "");

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
        var uncommented = System.Text.RegularExpressions.Regex.Replace(NotebookConfig.Starter, "^# ", "",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            // The example's page is the default, so it is swapped for bundle to show the line was actually read.
            .Replace("body: { resolve: page }", "body: { resolve: bundle }");

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
            files:
              include: ["**/*.md", "raw/**"]
              exclude: [".git/**", "inbox/**"]
            """);

        Assert.Equal(["**/*.md", "raw/**"], config.Include);
        Assert.Equal([".git/**", "inbox/**"], config.Exclude);
    }

    [Fact]
    public void Without_a_files_section_everything_is_included()
    {
        var config = NotebookConfig.Parse("links: { roots: [\"index.md\"] }\n");

        Assert.Equal(["**/*"], config.Include);
        Assert.Empty(config.Exclude);
    }

    [Fact]
    public void An_empty_config_is_valid()
    {
        var config = NotebookConfig.Parse("");

        Assert.Equal(["**/*"], config.Include);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var config = NotebookConfig.Parse("""
            files:
              include: ["**/*.md"]
              typo: true
            ids:
              field: sources[].id
            links:
              typo: 1
              body: { resolve: bundle, typo: 2 }
            """);

        Assert.Equal(["**/*.md"], config.Include);
        Assert.Equal(LinkBase.Bundle, config.Links.Body);
    }

    [Fact]
    public void Bundles_and_links_are_read()
    {
        var config = NotebookConfig.Parse("""
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
        var config = NotebookConfig.Parse("files: { include: [\"**/*\"] }\n");

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
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(yaml));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void The_link_fingerprint_changes_with_the_settings_that_shape_links_only()
    {
        string Fingerprint(string yaml) => NotebookConfig.Parse(yaml).LinkSettings.Fingerprint;
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
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("files: [unclosed\n"));

        Assert.Contains(".hippo.yaml", ex.Message);
    }

    [Fact]
    public void Deep_nesting_is_an_error_not_a_crash()
    {
        var yaml = $"a: {new string('[', 100_000)}{new string(']', 100_000)}\n";

        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse(yaml));

        Assert.Contains(".hippo.yaml", ex.Message);
    }

    [Fact]
    public void An_include_that_is_not_a_list_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => NotebookConfig.Parse("files:\n  include: \"**/*\"\n"));

        Assert.Contains("files.include", ex.Message);
    }

    [Fact]
    public void A_config_that_is_not_a_mapping_is_an_error()
    {
        Assert.Throws<HippoException>(() => NotebookConfig.Parse("- a\n- b\n"));
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
