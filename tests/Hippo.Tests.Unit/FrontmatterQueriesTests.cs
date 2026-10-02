using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Tests.Unit;

/// <summary>Runs <c>--where</c> conditions and <c>--field</c> paths against frontmatter as the index stores it.</summary>
public class FrontmatterQueriesTests
{
    private const string Page = """
        {"type":"Topic","title":"A","tags":["x","y"],"count":3,"ratio":0.5,"draft":true,"empty":null,
         "as_of":"2026-04-01","generated":{"at":"2026-09-01"},"sources":[{"id":"a"},{"id":"b"}],"rank":[2,10]}
        """;

    private static bool Matches(string condition, string? frontmatter = Page, string? parseError = null) =>
        FrontmatterFilter.MatchesAll([FrontmatterFilter.Parse(condition)], frontmatter, parseError);

    [Theory]
    [InlineData("type=Topic", "type", "Equal", "Topic")]
    [InlineData("title=a=b", "title", "Equal", "a=b")]
    [InlineData("status=", "status", "Equal", "")]
    [InlineData("tags!=draft", "tags", "NotEqual", "draft")]
    [InlineData("as_of<2026-04-01", "as_of", "Less", "2026-04-01")]
    [InlineData("as_of<=2026-04-01", "as_of", "LessOrEqual", "2026-04-01")]
    [InlineData("count>3", "count", "Greater", "3")]
    [InlineData("count>=3", "count", "GreaterOrEqual", "3")]
    [InlineData("a<b=c", "a", "Less", "b=c")]
    [InlineData("a=<b", "a", "Equal", "<b")]
    [InlineData("tracking", "tracking", "Present", "")]
    [InlineData("generated.at", "generated.at", "Present", "")]
    [InlineData("!as_of", "as_of", "Missing", "")]
    public void A_condition_reads_its_field_up_to_the_first_operator_character(
        string text, string field, string op, string value)
    {
        Assert.Equal(new FrontmatterFilter(field, Enum.Parse<FrontmatterOperator>(op), value), FrontmatterFilter.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("=Topic")]
    [InlineData("<3")]
    [InlineData("a..b=c")]
    [InlineData("a.=c")]
    [InlineData("a!b")]
    [InlineData("!")]
    [InlineData("!a=b")]
    [InlineData("!!a")]
    [InlineData("sources[].id=a")]
    public void A_malformed_condition_is_an_error(string text)
    {
        var error = Assert.Throws<HippoException>(() => FrontmatterFilter.Parse(text));
        Assert.Contains("--where", error.Message);
    }

    [Fact]
    public void Equals_matches_a_string_field()
    {
        Assert.True(Matches("type=Topic"));
        Assert.False(Matches("type=Person"));
    }

    [Fact]
    public void Equals_matches_booleans_and_null_by_their_text()
    {
        Assert.True(Matches("draft=true"));
        Assert.True(Matches("empty=null"));
    }

    [Fact]
    public void Equals_compares_a_number_as_a_number_when_the_value_is_one()
    {
        Assert.True(Matches("count=3"));
        Assert.True(Matches("count=3.0"));
        Assert.True(Matches("ratio=0.50"));
    }

    [Fact]
    public void Equals_compares_a_number_as_text_when_the_value_is_not_one()
    {
        Assert.False(Matches("count=three"));
        Assert.True(Matches("count=3", """{"count":"3"}"""));
        Assert.False(Matches("count=3.0", """{"count":"3"}"""));
    }

    [Fact]
    public void Equals_matches_an_element_of_a_list()
    {
        Assert.True(Matches("tags=x"));
        Assert.True(Matches("tags=y"));
        Assert.False(Matches("tags=z"));
    }

    [Fact]
    public void Equals_follows_dotted_fields_into_nested_mappings()
    {
        Assert.True(Matches("generated.at=2026-09-01"));
    }

    [Fact]
    public void Equals_on_a_mapping_or_a_missing_field_matches_nothing()
    {
        Assert.False(Matches("generated=2026-09-01"));
        Assert.False(Matches("status=draft"));
        Assert.False(Matches("type.name=Topic"));
    }

    [Fact]
    public void Not_equals_is_the_exact_negation_of_equals()
    {
        Assert.False(Matches("type!=Topic"));
        Assert.True(Matches("type!=Person"));
        Assert.False(Matches("tags!=x"));
        Assert.True(Matches("tags!=draft"));
        Assert.True(Matches("status!=draft"));
        Assert.True(Matches("generated!=2026-09-01"));
        Assert.False(Matches("count!=3.0"));
    }

    [Fact]
    public void Not_equals_matches_a_file_with_no_frontmatter()
    {
        Assert.True(Matches("tags!=draft", frontmatter: null));
    }

    [Theory]
    [InlineData("as_of<2026-05-01", true)]
    [InlineData("as_of<2026-04-01", false)]
    [InlineData("as_of<=2026-04-01", true)]
    [InlineData("as_of>2026-03-31", true)]
    [InlineData("as_of>=2026-04-02", false)]
    [InlineData("type>Topi", true)]
    [InlineData("type<topic", true)]
    public void A_range_compares_text_ordinally(string condition, bool expected)
    {
        Assert.Equal(expected, Matches(condition));
    }

    [Theory]
    [InlineData("count<10", true)]
    [InlineData("count>10", false)]
    [InlineData("count>=3", true)]
    [InlineData("count<=2.5", false)]
    [InlineData("ratio<1", true)]
    [InlineData("ratio>-1e3", true)]
    public void A_range_compares_a_number_as_a_number_when_the_value_is_one(string condition, bool expected)
    {
        Assert.Equal(expected, Matches(condition));
    }

    [Theory]
    [InlineData("id=1234567890123456789", false)]
    [InlineData("id!=1234567890123456789", true)]
    [InlineData("id=1234567890123456790", true)]
    [InlineData("id>1234567890123456789", true)]
    [InlineData("id<1234567890123456790", false)]
    public void Integers_past_two_to_the_53_compare_exactly(string condition, bool expected)
    {
        // Both of these round to the same double.
        Assert.Equal(expected, Matches(condition, """{"id":1234567890123456790}"""));
    }

    [Fact]
    public void A_range_on_a_number_compares_its_text_when_the_value_is_not_a_number()
    {
        // As text, "3" sorts after "10" but before "a".
        Assert.True(Matches("count<a"));
        Assert.True(Matches("count>1o"));
    }

    [Fact]
    public void A_range_on_a_list_matches_when_any_element_does()
    {
        Assert.True(Matches("rank>5"));
        Assert.True(Matches("rank<5"));
        Assert.False(Matches("rank>10"));
    }

    [Theory]
    [InlineData("status<z")]
    [InlineData("draft>a")]
    [InlineData("draft<z")]
    [InlineData("empty<z")]
    [InlineData("generated>a")]
    [InlineData("sources>a")]
    public void A_range_on_a_missing_field_a_boolean_null_or_a_mapping_never_matches(string condition)
    {
        Assert.False(Matches(condition));
    }

    [Fact]
    public void Present_needs_the_key_and_a_value_that_is_not_null()
    {
        Assert.True(Matches("type"));
        Assert.True(Matches("generated"));
        Assert.True(Matches("generated.at"));
        Assert.False(Matches("empty"));
        Assert.False(Matches("status"));
    }

    [Fact]
    public void Missing_is_the_exact_negation_of_present()
    {
        Assert.False(Matches("!type"));
        Assert.False(Matches("!generated.at"));
        Assert.True(Matches("!empty"));
        Assert.True(Matches("!status"));
        Assert.True(Matches("!status", frontmatter: null));
    }

    [Theory]
    [InlineData("type=Topic")]
    [InlineData("type!=Topic")]
    [InlineData("count<10")]
    [InlineData("type")]
    [InlineData("!type")]
    public void Frontmatter_that_failed_to_parse_matches_no_condition(string condition)
    {
        Assert.False(Matches(condition, frontmatter: null, parseError: "line 2: bad"));
    }

    [Fact]
    public void A_file_must_meet_every_condition()
    {
        FrontmatterFilter[] both = [FrontmatterFilter.Parse("type=Topic"), FrontmatterFilter.Parse("as_of<2026-05-01")];
        FrontmatterFilter[] one = [FrontmatterFilter.Parse("type=Topic"), FrontmatterFilter.Parse("as_of<2026-01-01")];

        Assert.True(FrontmatterFilter.MatchesAll(both, Page, null));
        Assert.False(FrontmatterFilter.MatchesAll(one, Page, null));
    }

    [Fact]
    public void No_conditions_match_every_file()
    {
        Assert.True(FrontmatterFilter.MatchesAll([], null, "line 2: bad"));
    }

    [Fact]
    public void Field_paths_split_at_commas_across_every_value_and_keep_the_order_given()
    {
        Assert.Equal(["as_of", "tracking", "verified.at"], FrontmatterFields.ParsePaths(["as_of,tracking", "verified.at", "as_of"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("as_of,")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("sources[].id")]
    public void A_malformed_field_path_is_an_error(string value)
    {
        var error = Assert.Throws<HippoException>(() => FrontmatterFields.ParsePaths([value]));
        Assert.Contains("--field", error.Message);
    }

    private static string Read(string frontmatter, params string[] paths) =>
        JsonSerializer.Serialize(FrontmatterFields.Read(paths, frontmatter));

    [Fact]
    public void Read_keys_each_value_by_the_path_as_given()
    {
        Assert.Equal("""{"type":"Topic","generated.at":"2026-09-01","count":3}""", Read(Page, "type", "generated.at", "count"));
    }

    [Fact]
    public void Read_returns_a_list_or_mapping_whole()
    {
        Assert.Equal("""{"sources":[{"id":"a"},{"id":"b"}],"generated":{"at":"2026-09-01"}}""", Read(Page, "sources", "generated"));
    }

    [Fact]
    public void Read_leaves_out_a_missing_field_and_keeps_a_null_one()
    {
        Assert.Equal("""{"empty":null}""", Read(Page, "status", "empty", "type.name"));
    }

    [Fact]
    public void Read_without_frontmatter_is_empty()
    {
        Assert.Empty(FrontmatterFields.Read(["type"], null));
    }
}
