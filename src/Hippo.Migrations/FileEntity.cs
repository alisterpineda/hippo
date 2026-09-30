namespace Hippo.Migrations;

/// <summary>One row per indexed workspace file.</summary>
public sealed class FileEntity
{
    public long Id { get; set; }

    /// <summary>Workspace-relative path with <c>/</c> separators.</summary>
    public required string Path { get; set; }

    /// <summary>Last write time in 100 ns ticks since the Unix epoch, UTC.</summary>
    public long Mtime { get; set; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Lowercase hex SHA-256 of the content.</summary>
    public required string Hash { get; set; }

    /// <summary>When the content was last hashed, in the same ticks as <see cref="Mtime"/>.</summary>
    public long HashedAt { get; set; }

    /// <summary><c>markdown</c> or <c>other</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The frontmatter as a JSON object; null when the file has none or it failed to parse.</summary>
    public string? Frontmatter { get; set; }

    /// <summary>Why the frontmatter failed to parse; null when it parsed or there was none.</summary>
    public string? ParseError { get; set; }
}
