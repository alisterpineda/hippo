using Hippo.Commands.Self;

namespace Hippo.Tests.Unit;

public class SemVerTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0, "", "")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1", "")]
    [InlineData("0.2.0-dev+abc1234", 0, 2, 0, "dev", "abc1234")]
    [InlineData("10.20.30+build.5", 10, 20, 30, "", "build.5")]
    [InlineData("1.0.0-alpha-1.x.0", 1, 0, 0, "alpha-1.x.0", "")]
    public void Parses_each_part(string text, int major, int minor, int patch, string prerelease, string metadata)
    {
        var version = SemVer.TryParse(text);

        Assert.NotNull(version);
        Assert.Equal((major, minor, patch, prerelease, metadata), (version.Major, version.Minor, version.Patch, version.Prerelease, version.BuildMetadata));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc.01")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3 ")]
    [InlineData("nope")]
    [InlineData("2147483648.0.0")]
    [InlineData("1.0.1\u0663")]
    [InlineData("1.0.0-rc.\u0663")]
    public void Refuses_what_is_not_a_version(string text)
    {
        Assert.Null(SemVer.TryParse(text));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("1.2.3-rc.1+abc", "1.2.3-rc.1+abc")]
    public void Prints_as_parsed(string text, string expected)
    {
        Assert.Equal(expected, SemVer.TryParse(text)!.ToString());
    }

    [Fact]
    public void Without_metadata_keeps_the_rest()
    {
        Assert.Equal("0.2.0-dev", SemVer.TryParse("0.2.0-dev+abc1234")!.WithoutMetadata().ToString());
    }

    /// <summary>Each pair in ascending order, from the SemVer 2.0 examples and hippo's own version scheme.</summary>
    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0", "2.1.0")]
    [InlineData("2.1.0", "2.1.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.2")]
    [InlineData("0.1.0-rc.2", "0.1.0-rc.10")]
    [InlineData("0.1.0-rc.10", "0.1.0")]
    [InlineData("0.2.0-dev", "0.2.0-rc.1")]
    [InlineData("0.1.0", "0.2.0-dev")]
    [InlineData("0.2.0-1", "0.2.0-dev")]
    public void Orders_by_precedence(string lower, string higher)
    {
        var a = SemVer.TryParse(lower)!;
        var b = SemVer.TryParse(higher)!;

        Assert.True(a.CompareTo(b) < 0, $"{lower} < {higher}");
        Assert.True(b.CompareTo(a) > 0, $"{higher} > {lower}");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0+build")]
    [InlineData("1.0.0-rc.1+a", "1.0.0-rc.1+b")]
    public void Ignores_build_metadata_in_precedence(string a, string b)
    {
        Assert.Equal(0, SemVer.TryParse(a)!.CompareTo(SemVer.TryParse(b)!));
    }
}
