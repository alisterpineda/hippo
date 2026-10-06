using System.Text.RegularExpressions;
using Hippo.Okf;

namespace Hippo.Workspaces;

/// <summary>A rule <c>hippo lint</c> checks. An OKF rule's name starts <c>okf-</c>; a workspace rule's has no prefix.
/// <see cref="Description"/> says on one line what it reports, for <c>hippo lint --help</c>.</summary>
internal sealed record LintRule(string Name, string Description)
{
    public bool IsOkf => Name.StartsWith("okf-", StringComparison.Ordinal);
}

/// <summary>Every rule <c>hippo lint</c> checks: OKF's, in every OKF bundle, then the workspace's, everywhere. Every rule
/// can be turned off with <c>lint.off</c>, OKF's MUST rules too.</summary>
internal static class LintRules
{
    public const string BrokenLink = "broken-link";
    public const string FrontmatterSyntax = "frontmatter-syntax";

    public static IReadOnlyList<LintRule> All { get; } =
    [
        new(OkfRules.Type,
            "A concept with no frontmatter, frontmatter that fails to parse, or no non-empty type"),
        new(OkfRules.IndexFrontmatter,
            "Frontmatter in an index.md below the bundle root, or a key other than okf_version in the root one"),
        new(OkfRules.LogDate,
            "A level-2 heading in a log.md that is not a YYYY-MM-DD date"),
        new(OkfRules.SourceResource,
            "A sources entry with no resource"),
        new(OkfRules.Footnote,
            "A footnote whose label matches no sources[].id on its page"),
        new(OkfRules.Timestamp,
            "A generated.at, verified[].at, stale_after, sources[].last_modified or usage_window that is not an ISO 8601 datetime with a UTC offset"),
        new(OkfRules.Actor,
            "A generated with no by, or a generated.by or verified[].by not shaped <producer>/<version>, human:<id> or process:<id>"),
        new(OkfRules.Status,
            "A status other than draft, stable or deprecated"),
        new(OkfRules.Index,
            "An index.md entry whose page is missing or whose description is not the page's, or a page no index.md above it links to"),
        new(OkfRules.Version,
            $"A bundle whose okf_version is not {OkfBundle.SpecVersion}, which hippo reads as {OkfBundle.SpecVersion} all the same"),
        new(BrokenLink,
            "A path link whose target is neither an indexed file nor a folder holding one, or that leaves the workspace"),
        new(FrontmatterSyntax,
            "A markdown file whose frontmatter fails to parse"),
    ];

    public static string[] Names { get; } = All.Select(rule => rule.Name).ToArray();

    /// <summary>The names of the rules <paramref name="pattern"/> matches, in the order of <see cref="All"/>: a rule's
    /// name, or a pattern in which <c>*</c> matches any run of characters, as <c>okf-*</c> matches every OKF rule.</summary>
    public static List<string> Match(string pattern)
    {
        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
        return Names.Where(name => regex.IsMatch(name)).ToList();
    }

    /// <summary>Why <paramref name="pattern"/>, which <see cref="Match"/> found no rule for, is refused.</summary>
    public static string NoMatch(string pattern) => $"no rule matches {pattern}; expected one of {string.Join(", ", Names)}";
}
