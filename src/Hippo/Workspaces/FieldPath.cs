namespace Hippo.Workspaces;

/// <summary>One part of a <see cref="FieldPath"/>: a key of a mapping, and whether it ends in <c>[]</c>, standing for
/// each element of the list that key holds.</summary>
internal readonly record struct FieldPathPart(string Name, bool Each);

/// <summary>
/// A dotted path into frontmatter, as a <c>links.frontmatter</c> field, <c>--where</c> and <c>--field</c> take it: each
/// part a key of the mapping the part before it names, and a part ending in <c>[]</c> each element of the list that key
/// holds, as in <c>sources[].resource</c>. A part without <c>[]</c> does not step into a list, and <c>[]</c> on anything
/// but a list reaches nothing. A key holding <c>.</c>, <c>[</c> or <c>]</c> cannot be reached.
/// </summary>
internal sealed record FieldPath(string Text)
{
    public IReadOnlyList<FieldPathPart> Parts { get; } = Text.Split('.')
        .Select(part => part.EndsWith("[]", StringComparison.Ordinal) ? new FieldPathPart(part[..^2], true) : new FieldPathPart(part, false))
        .ToList();

    /// <summary>Whether every part has a name, and no name holds a bracket, so the only brackets are each part's
    /// closing <c>[]</c>.</summary>
    public bool IsValid => Parts.All(part => part.Name.Length > 0 && part.Name.IndexOfAny(['[', ']']) < 0);

    /// <summary>Whether a part ends in <c>[]</c>, so the path can reach any number of values.</summary>
    public bool IsEach => Parts.Any(part => part.Each);

    /// <summary>Equal by <see cref="Text"/> alone, since the record's own equality would compare <see cref="Parts"/> by
    /// reference; this keeps a record that holds a path equal to another holding one of the same text.</summary>
    public bool Equals(FieldPath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;
}
