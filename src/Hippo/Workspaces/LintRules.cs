using Hippo.Okf;

namespace Hippo.Workspaces;

/// <summary>A rule <c>hippo lint</c> checks. An OKF rule's name starts <c>okf-</c>; a workspace rule's has no prefix.
/// <see cref="CanTurnOff"/> says whether <c>lint.off</c> may name it.</summary>
internal sealed record LintRule(string Name, bool CanTurnOff)
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
        new(OkfRules.Type, CanTurnOff: false),             // MUST
        new(OkfRules.IndexFrontmatter, CanTurnOff: false), // MUST
        new(OkfRules.LogDate, CanTurnOff: false),          // MUST
        new(OkfRules.SourceResource, CanTurnOff: true),    // SHOULD
        new(OkfRules.Footnote, CanTurnOff: true),          // SHOULD
        new(OkfRules.Timestamp, CanTurnOff: true),         // SHOULD
        new(OkfRules.Actor, CanTurnOff: true),             // SHOULD
        new(OkfRules.Status, CanTurnOff: true),            // SHOULD
        new(OkfRules.Index, CanTurnOff: true),             // SHOULD
        new(BrokenLink, CanTurnOff: true),
        new(FrontmatterSyntax, CanTurnOff: true),
    ];

    public static string[] Names { get; } = All.Select(rule => rule.Name).ToArray();

    public static LintRule? Find(string name) => All.FirstOrDefault(rule => rule.Name == name);
}
