namespace Hippo.Migrations;

/// <summary>One row per link out of a markdown page.</summary>
public sealed class LinkEntity
{
    public long Id { get; set; }

    /// <summary>The page the link is in.</summary>
    public long SourceId { get; set; }

    /// <summary>1-based line of the link in its page.</summary>
    public long Line { get; set; }

    /// <summary><c>body</c> or <c>frontmatter</c>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>path</c>, <c>url</c> or <c>anchor</c>. Whether a path link's target is a file, a directory or
    /// missing is read from <c>files</c> when asked (see <c>LinkQueries</c>), so it stays right when the target comes
    /// or goes without the page changing.</summary>
    public required string Type { get; set; }

    /// <summary>The link as written.</summary>
    public required string Raw { get; set; }

    /// <summary>The workspace key a path link resolves to; null for a path that leaves the workspace, and for every
    /// other type.</summary>
    public string? Target { get; set; }
}
