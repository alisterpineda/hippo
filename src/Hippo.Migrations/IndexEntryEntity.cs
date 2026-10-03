namespace Hippo.Migrations;

/// <summary>One row per entry in an OKF bundle's <c>index.md</c>: a list item that opens with a link to a path in the
/// workspace (OKF §8).</summary>
public sealed class IndexEntryEntity
{
    public long Id { get; set; }

    /// <summary>The <c>index.md</c> the entry is in.</summary>
    public long FileId { get; set; }

    /// <summary>1-based line of the entry's link in its file.</summary>
    public long Line { get; set; }

    /// <summary>The workspace key the entry's link resolves to.</summary>
    public required string Target { get; set; }

    /// <summary><see cref="Target"/> in Unicode canonical decomposition (NFD), matched against a file's
    /// <c>PathNfd</c>.</summary>
    public required string TargetNfd { get; set; }

    /// <summary>The entry's text after its link and separator, as written; null when it has none.</summary>
    public string? Description { get; set; }
}
