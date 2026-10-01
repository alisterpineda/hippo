namespace Hippo.Migrations;

/// <summary>One row per finding a lint rule reports on a file.</summary>
public sealed class FindingEntity
{
    public long Id { get; set; }

    /// <summary>The file the finding is on.</summary>
    public long FileId { get; set; }

    /// <summary>The rule's name, such as <c>okf-type</c>.</summary>
    public required string Rule { get; set; }

    /// <summary>1-based line in the file; null when the finding is about the whole file, such as missing frontmatter.</summary>
    public long? Line { get; set; }

    public required string Message { get; set; }

    /// <summary>The other workspace paths the finding concerns, as a JSON array of strings.</summary>
    public required string Related { get; set; }
}
