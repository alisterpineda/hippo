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
            bundles:
              - root: wiki
            lint:
              rules: [placement]
            """, warnings);

        Assert.Equal(["**/*"], config.Include);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'bundles'"));
        Assert.Contains(warnings, w => w.Contains("'lint'"));
        Assert.Contains(warnings, w => w.Contains("'files.typo'"));
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
