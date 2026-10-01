namespace Hippo.Okf;

/// <summary>How binding a rule is in OKF v0.2. A MUST rule decides conformance (§11), so it cannot be turned off; a
/// SHOULD rule can, with <c>lint.off</c>.</summary>
internal enum RuleLevel
{
    Must,
    Should,
}

internal sealed record OkfRule(string Name, RuleLevel Level);

/// <summary>The rules <c>hippo lint</c> checks in every OKF bundle.</summary>
internal static class OkfRules
{
    public const string Type = "okf-type";
    public const string IndexFrontmatter = "okf-index-frontmatter";
    public const string LogDate = "okf-log-date";
    public const string SourceResource = "okf-source-resource";
    public const string Footnote = "okf-footnote";
    public const string Timestamp = "okf-timestamp";
    public const string Actor = "okf-actor";
    public const string Status = "okf-status";

    public static IReadOnlyList<OkfRule> All { get; } =
    [
        new(Type, RuleLevel.Must),
        new(IndexFrontmatter, RuleLevel.Must),
        new(LogDate, RuleLevel.Must),
        new(SourceResource, RuleLevel.Should),
        new(Footnote, RuleLevel.Should),
        new(Timestamp, RuleLevel.Should),
        new(Actor, RuleLevel.Should),
        new(Status, RuleLevel.Should),
    ];

    public static string[] Names { get; } = All.Select(rule => rule.Name).ToArray();

    public static OkfRule? Find(string name) => All.FirstOrDefault(rule => rule.Name == name);
}
