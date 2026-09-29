namespace Hippo.Migrations;

/// <summary>A fact about how the index was built, such as the link settings its links were extracted under.</summary>
public sealed class MetaEntity
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
