using Hippo.Okf;

namespace Hippo.Workspaces;

/// <summary>A rule <c>hippo lint</c> checks. An OKF rule's name starts <c>okf-</c>; a workspace rule's has no prefix.
/// <see cref="CanTurnOff"/> says whether <c>lint.off</c> may name it. <see cref="Description"/> says on one line what
/// it reports, for <c>hippo lint --help</c>.</summary>
internal sealed record LintRule(string Name, bool CanTurnOff, string Description)
{
    public bool IsOkf => Name.StartsWith("okf-", StringComparison.Ordinal);
}

/// <summary>Every rule <c>hippo lint</c> checks: OKF's, in every OKF bundle, then the workspace's, everywhere. An OKF
/// MUST rule decides conformance (§11), so it cannot be turned off; every other rule can, with <c>lint.off</c>.</summary>
internal static class LintRules
{
    public const string BrokenLink = "broken-link";
    public const string FrontmatterSyntax = "frontmatter-syntax";

    public static IReadOnlyList<LintRule> All { get; } =
    [
        new(OkfRules.Type, CanTurnOff: false,              // MUST
            "A concept with no frontmatter, frontmatter that fails to parse, or no non-empty type"),
        new(OkfRules.IndexFrontmatter, CanTurnOff: false,  // MUST
            "Frontmatter in an index.md below the bundle root, or a key other than okf_version in the root one"),
        new(OkfRules.LogDate, CanTurnOff: false,           // MUST
            "A level-2 heading in a log.md that is not a YYYY-MM-DD date"),
        new(OkfRules.SourceResource, CanTurnOff: true,     // SHOULD
            "A sources entry with no resource"),
        new(OkfRules.Footnote, CanTurnOff: true,           // SHOULD
            "A footnote whose label matches no sources[].id on its page"),
        new(OkfRules.Timestamp, CanTurnOff: true,          // SHOULD
            "A generated.at, verified[].at, stale_after, sources[].last_modified or usage_window that is not an ISO 8601 datetime with a UTC offset"),
        new(OkfRules.Actor, CanTurnOff: true,              // SHOULD
            "A generated with no by, or a generated.by or verified[].by not shaped <producer>/<version>, human:<id> or process:<id>"),
        new(OkfRules.Status, CanTurnOff: true,             // SHOULD
            "A status other than draft, stable or deprecated"),
        new(OkfRules.Index, CanTurnOff: true,              // SHOULD
            "An index.md entry whose page is missing or whose description is not the page's, or a page no index.md above it links to"),
        new(BrokenLink, CanTurnOff: true,
            "A path link whose target is neither an indexed file nor a folder holding one, or that leaves the workspace"),
        new(FrontmatterSyntax, CanTurnOff: true,
            "A markdown file whose frontmatter fails to parse"),
    ];

    public static string[] Names { get; } = All.Select(rule => rule.Name).ToArray();

    public static LintRule? Find(string name) => All.FirstOrDefault(rule => rule.Name == name);
}
