namespace Hippo.Okf;

/// <summary>The rules <c>hippo lint</c> checks in every OKF bundle. <see cref="Workspaces.LintRules"/> lists them with
/// the workspace's own.</summary>
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
    public const string Index = "okf-index";
}
