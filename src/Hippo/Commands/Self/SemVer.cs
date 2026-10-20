using System.Globalization;
using System.Text.RegularExpressions;

namespace Hippo.Commands.Self;

/// <summary>
/// A version as Semantic Versioning 2.0.0 defines it, with its precedence: hippo's own version, the one the user names,
/// and the one in the tag the latest release redirects to. <see cref="Prerelease"/> and <see cref="BuildMetadata"/>
/// are empty when absent. Precedence ignores build metadata, so a <c>+</c> suffix compares equal to none.
/// </summary>
internal sealed partial record SemVer(int Major, int Minor, int Patch, string Prerelease, string BuildMetadata) : IComparable<SemVer>
{
    /// <summary>Parses <paramref name="text"/> as a strict SemVer 2.0.0 version, with no leading <c>v</c> and no
    /// surrounding space; null when it is not one, or when a number is too large to hold.</summary>
    public static SemVer? TryParse(string text)
    {
        var match = Grammar().Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }
        return new SemVer(major, minor, patch, match.Groups["prerelease"].Value, match.Groups["metadata"].Value);
    }

    public SemVer WithoutMetadata() => this with { BuildMetadata = "" };

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
        + (Prerelease.Length == 0 ? "" : "-" + Prerelease)
        + (BuildMetadata.Length == 0 ? "" : "+" + BuildMetadata);

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }
        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }
        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }
        if (core != 0)
        {
            return core;
        }
        // A prerelease sorts below the release it precedes.
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
        {
            return (Prerelease.Length == 0).CompareTo(other.Prerelease.Length == 0);
        }
        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var order = CompareIdentifiers(mine[i], theirs[i]);
            if (order != 0)
            {
                return order;
            }
        }
        // Every shared identifier equal: the longer list is the higher version.
        return mine.Length.CompareTo(theirs.Length);
    }

    /// <summary>One prerelease identifier against another: numeric ones by value and below alphanumeric ones, which
    /// compare by ASCII. A numeric identifier has no leading zero, so its value orders by length then digits.</summary>
    private static int CompareIdentifiers(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            var byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
        }
        if (aNumeric != bNumeric)
        {
            return aNumeric ? -1 : 1;
        }
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    // The grammar semver.org gives, with the identifiers named and its \d written as [0-9], since .NET's \d also
    // matches digits from other scripts.
    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)"
        + @"(?:-(?<prerelease>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + @"(?:\+(?<metadata>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$")]
    private static partial Regex Grammar();
}
