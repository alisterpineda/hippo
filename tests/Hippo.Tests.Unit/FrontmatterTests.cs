using Hippo.Notebooks;

namespace Hippo.Tests.Unit;

public class FrontmatterTests
{
    [Fact]
    public void Mapping_becomes_a_json_object()
    {
        var result = Frontmatter.Parse("---\ntitle: Hello\ntags: [a, b]\n---\n# Body\n");

        Assert.Null(result.Error);
        Assert.Equal("""{"title":"Hello","tags":["a","b"]}""", result.Json);
    }

    [Fact]
    public void Plain_scalars_resolve_to_json_types()
    {
        var result = Frontmatter.Parse("---\ncount: 42\nratio: 1.5\ndone: true\nnothing: null\nempty:\nday: 2026-09-28\n---\n");

        Assert.Equal("""{"count":42,"ratio":1.5,"done":true,"nothing":null,"empty":null,"day":"2026-09-28"}""", result.Json);
    }

    [Fact]
    public void Quoted_scalars_stay_strings()
    {
        var result = Frontmatter.Parse("---\ncount: \"42\"\ndone: 'true'\n---\n");

        Assert.Equal("""{"count":"42","done":"true"}""", result.Json);
    }

    [Fact]
    public void Nested_mappings_and_sequences_are_kept()
    {
        var result = Frontmatter.Parse("---\nsources:\n  - id: j-1\n    resource: raw/a.md\ngenerated:\n  at: 2026-09-01\n---\n");

        Assert.Equal("""{"sources":[{"id":"j-1","resource":"raw/a.md"}],"generated":{"at":"2026-09-01"}}""", result.Json);
    }

    [Fact]
    public void A_file_without_frontmatter_has_none_and_no_error()
    {
        var result = Frontmatter.Parse("# Just a heading\n\n---\n\ntext\n");

        Assert.Null(result.Json);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Empty_frontmatter_is_an_empty_object()
    {
        var result = Frontmatter.Parse("---\n---\nbody\n");

        Assert.Equal("{}", result.Json);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Crlf_line_endings_and_a_byte_order_mark_are_accepted()
    {
        var result = Frontmatter.Parse("\uFEFF---\r\ntitle: Hello\r\n---\r\nbody\r\n");

        Assert.Equal("""{"title":"Hello"}""", result.Json);
    }

    [Fact]
    public void Invalid_yaml_is_an_error_not_an_exception()
    {
        var result = Frontmatter.Parse("---\ntitle: [unclosed\n---\n");

        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_unclosed_block_is_an_error()
    {
        var result = Frontmatter.Parse("---\ntitle: Hello\n\nbody with no closing line\n");

        Assert.Null(result.Json);
        Assert.Equal("frontmatter opened on line 1 is never closed", result.Error);
    }

    [Fact]
    public void A_sequence_at_the_top_is_an_error()
    {
        var result = Frontmatter.Parse("---\n- a\n- b\n---\n");

        Assert.Null(result.Json);
        Assert.Equal("frontmatter is not a mapping", result.Error);
    }

    [Fact]
    public void A_duplicate_key_is_an_error()
    {
        var result = Frontmatter.Parse("---\ntitle: a\ntitle: b\n---\n");

        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Error_line_numbers_count_from_the_top_of_the_file()
    {
        var result = Frontmatter.Parse("---\ntitle: ok\nbad: [unclosed\n---\n");

        Assert.StartsWith("line 4: ", result.Error);
        Assert.DoesNotContain("Idx", result.Error);
    }

    [Theory]
    [InlineData("---\ntitle: a\ntitle: b\n---\n", "line 3: ")]
    [InlineData("---\ntitle: a\n? [x]\n: b\n---\n", "line 3: ")]
    public void Key_errors_name_the_line_of_the_key(string text, string prefix)
    {
        var result = Frontmatter.Parse(text);

        Assert.StartsWith(prefix, result.Error);
    }

    [Theory]
    [InlineData("0o17", "15")]
    [InlineData("0x1F", "31")]
    [InlineData("!!str 42", "\"42\"")]
    [InlineData("! 42", "\"42\"")]
    [InlineData("~", "null")]
    public void Octal_hex_tags_and_tilde_resolve(string yaml, string json)
    {
        var result = Frontmatter.Parse($"---\nv: {yaml}\n---\n");

        Assert.Equal($$"""{"v":{{json}}}""", result.Json);
    }

    [Fact]
    public void An_alias_bomb_is_an_error_not_a_hang()
    {
        var yaml = new System.Text.StringBuilder("---\na0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i < 9; i++)
        {
            yaml.Append($"a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 10))}]\n");
        }
        yaml.Append("---\n");

        var result = Frontmatter.Parse(yaml.ToString());

        Assert.Null(result.Json);
        Assert.Contains("expands too far", result.Error);
    }

    [Fact]
    public void Deep_nesting_is_an_error_not_a_crash()
    {
        var result = Frontmatter.Parse($"---\na: {new string('[', 100_000)}{new string(']', 100_000)}\n---\n");

        Assert.Null(result.Json);
        Assert.Contains("nested too deeply", result.Error);
    }

    [Fact]
    public void Self_referencing_aliases_are_an_error_not_a_crash()
    {
        var result = Frontmatter.Parse("---\na: &x [*x]\n---\n");

        Assert.Null(result.Json);
        Assert.NotNull(result.Error);
    }
}
