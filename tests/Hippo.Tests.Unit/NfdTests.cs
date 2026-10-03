using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

/// <summary>hippo runs with invariant globalization, where <see cref="string.Normalize()"/> returns its input, so
/// <see cref="Nfd"/> carries its own tables. The expected values are Unicode's own decompositions.</summary>
public class NfdTests
{
    [Fact]
    public void Text_with_nothing_to_decompose_is_unchanged()
    {
        Assert.Equal("wiki/people/cafe.md", Nfd.Of("wiki/people/cafe.md"));
    }

    [Fact]
    public void A_precomposed_letter_is_decomposed()
    {
        Assert.Equal("café.md", Nfd.Of("café.md"));
    }

    [Fact]
    public void A_decomposed_letter_is_kept()
    {
        Assert.Equal("café.md", Nfd.Of("café.md"));
    }

    [Fact]
    public void A_decomposition_is_applied_all_the_way_down()
    {
        // U+1EC7 is U+1EB9 (e with dot below) + U+0302, and U+1EB9 is e + U+0323.
        Assert.Equal("ệ", Nfd.Of("ệ"));
    }

    [Fact]
    public void A_singleton_decomposes_to_its_equivalent()
    {
        // The angstrom sign is canonically A with ring above.
        Assert.Equal("Å", Nfd.Of("Å"));
    }

    [Fact]
    public void A_hangul_syllable_decomposes_into_its_jamo()
    {
        // 한 is ㅎ + ㅏ + ㄴ; 가 has no final consonant.
        Assert.Equal("한가", Nfd.Of("한가"));
    }

    [Fact]
    public void Combining_marks_are_put_in_canonical_order()
    {
        // U+0323 (class 220) sorts before U+0302 (class 230), whichever is written first.
        Assert.Equal("ậ", Nfd.Of("ậ"));
    }

    [Fact]
    public void Marks_of_one_class_keep_their_order()
    {
        Assert.Equal("á̂", Nfd.Of("á̂"));
        Assert.Equal("ấ", Nfd.Of("ấ"));
    }

    [Fact]
    public void A_long_run_of_marks_is_put_in_order_in_reasonable_time()
    {
        // Alternating classes 230 and 220 is the worst case for an insertion sort: quadratic, minutes at this length.
        const int pairs = 100_000;
        var text = "a" + string.Concat(Enumerable.Repeat("̖́", pairs));
        var expected = "a" + new string('̖', pairs) + new string('́', pairs);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var actual = Nfd.Of(text);
        watch.Stop();

        Assert.Equal(expected, actual);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_character_outside_the_basic_plane_decomposes()
    {
        // U+1D15E MUSICAL SYMBOL HALF NOTE is U+1D157 + U+1D165.
        Assert.Equal("\U0001D157\U0001D165", Nfd.Of("\U0001D15E"));
    }

    [Fact]
    public void A_lone_surrogate_is_kept()
    {
        Assert.Equal("a\uD800b", Nfd.Of("a\uD800b"));
    }
}
