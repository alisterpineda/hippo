using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Hippo.Workspaces;

/// <summary>
/// Passes a YAML parser's events through, throwing a <see cref="YamlException"/> once mappings and sequences nest
/// deeper than <paramref name="maxDepth"/>. <c>YamlStream</c> builds its node tree recursively, so without this limit a
/// deeply nested block overflows the stack, which .NET cannot catch.
/// </summary>
internal sealed class DepthLimitedParser(IParser inner, int maxDepth) : IParser
{
    private int _depth;

    public ParsingEvent? Current => inner.Current;

    public bool MoveNext()
    {
        if (!inner.MoveNext())
        {
            return false;
        }
        var current = inner.Current;
        if (current is MappingStart or SequenceStart)
        {
            if (++_depth > maxDepth)
            {
                throw new YamlException(current.Start, current.End, "nested too deeply");
            }
        }
        else if (current is MappingEnd or SequenceEnd)
        {
            _depth--;
        }
        return true;
    }
}
