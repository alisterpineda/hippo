using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Hippo.Workspaces;

namespace Hippo.Indexing;

/// <summary>A dotted path into a file's frontmatter, as <c>--where</c> and <c>--field</c> take it, in the config's
/// grammar (<see cref="FrontmatterLinkField"/>): each part a key of the mapping the part before it names, and a part
/// ending in <c>[]</c> each element of the list that key holds. A part without <c>[]</c> does not step into a list, and
/// <c>[]</c> on anything but a list reaches nothing. A key holding <c>.</c>, <c>[</c> or <c>]</c> cannot be
/// reached.</summary>
internal sealed record FrontmatterPath(string Text)
{
    private readonly IReadOnlyList<(string Name, bool Each)> _parts = FrontmatterLinkField.Split(Text);

    /// <summary>Whether <see cref="Text"/> has no empty part and no bracket but a closing <c>[]</c>.</summary>
    public bool IsValid => FrontmatterLinkField.IsValidPath(_parts);

    /// <summary>Whether a part ends in <c>[]</c>, so the path can reach any number of values.</summary>
    public bool IsEach => _parts.Any(part => part.Each);

    /// <summary>Equal by <see cref="Text"/> alone, since the record's own equality would compare the parsed parts by
    /// reference; this keeps <see cref="FrontmatterFilter"/>, which holds its paths, equal to another of the same
    /// text.</summary>
    public bool Equals(FrontmatterPath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>Every value the path reaches in <paramref name="frontmatter"/>: none when a key on the way is missing
    /// or names something of another shape than the path says, and at most one when no part ends in <c>[]</c>.</summary>
    public IEnumerable<JsonElement> Find(JsonElement frontmatter) => Find(frontmatter, 0);

    private IEnumerable<JsonElement> Find(JsonElement value, int index)
    {
        if (index == _parts.Count)
        {
            yield return value;
            yield break;
        }
        var (name, each) = _parts[index];
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child))
        {
            yield break;
        }
        if (!each)
        {
            foreach (var found in Find(child, index + 1))
            {
                yield return found;
            }
            yield break;
        }
        if (child.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var item in child.EnumerateArray())
        {
            foreach (var found in Find(item, index + 1))
            {
                yield return found;
            }
        }
    }
}

internal enum FrontmatterOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, Present, Missing }

/// <summary>
/// One <c>--where</c> condition on a frontmatter <see cref="Field"/>, against the literal <see cref="Value"/>, or when
/// <see cref="Reference"/> is set, against that field of the same file. Two numbers compare as numbers, and anything
/// else as ordinal text, so ISO-8601 dates order correctly. A list on either side matches when any pair of elements
/// does, so a missing right-hand field meets no pair. <see cref="FrontmatterOperator.NotEqual"/> and
/// <see cref="FrontmatterOperator.Missing"/> are the exact negations of <see cref="FrontmatterOperator.Equal"/> and
/// <see cref="FrontmatterOperator.Present"/>, so a file without the field meets them, but frontmatter that failed to
/// parse meets no condition at all.
/// </summary>
internal sealed record FrontmatterFilter(string Field, FrontmatterOperator Operator, string Value, string? Reference = null)
{
    private static readonly char[] OperatorStarts = ['=', '<', '>', '!'];

    private readonly FrontmatterPath _field = new(Field);

    private readonly FrontmatterPath? _reference = Reference is null ? null : new(Reference);

    /// <summary><see cref="Value"/> as a side of a comparison.</summary>
    private readonly Operand _value = Operand.Of(Value);

    /// <summary>Parses <c>field</c>, <c>!field</c>, or a field, an operator (<c>=</c>, <c>!=</c>, <c>&lt;</c>,
    /// <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) and a value. The field ends at the first character that can start an
    /// operator, so it can hold none of them; the value can hold anything. A value starting with <c>@</c> names a field
    /// instead, and one starting with <c>@@</c> is a literal starting with <c>@</c>.</summary>
    public static FrontmatterFilter Parse(string text)
    {
        var at = text.IndexOfAny(OperatorStarts);
        var next = at >= 0 && at + 1 < text.Length ? text[at + 1] : '\0';
        var (field, op, value) = at switch
        {
            < 0 => (text, FrontmatterOperator.Present, ""),
            0 when text[0] == '!' && text.IndexOfAny(OperatorStarts, 1) < 0 => (text[1..], FrontmatterOperator.Missing, ""),
            _ => (text[at], next) switch
            {
                ('=', _) => (text[..at], FrontmatterOperator.Equal, text[(at + 1)..]),
                ('!', '=') => (text[..at], FrontmatterOperator.NotEqual, text[(at + 2)..]),
                ('<', '=') => (text[..at], FrontmatterOperator.LessOrEqual, text[(at + 2)..]),
                ('<', _) => (text[..at], FrontmatterOperator.Less, text[(at + 1)..]),
                ('>', '=') => (text[..at], FrontmatterOperator.GreaterOrEqual, text[(at + 2)..]),
                ('>', _) => (text[..at], FrontmatterOperator.Greater, text[(at + 1)..]),
                _ => ("", FrontmatterOperator.Equal, ""),
            },
        };
        (string Literal, string? Reference) operand = value.StartsWith("@@", StringComparison.Ordinal) ? (value[1..], null)
            : value.StartsWith('@') ? ("", value[1..])
            : (value, null);
        var (literal, reference) = operand;
        if (!new FrontmatterPath(field).IsValid || reference is not null && !new FrontmatterPath(reference).IsValid)
        {
            throw new HippoException(
                "--where expects <field>, !<field>, or <field> then =, !=, <, <=, > or >= and a value or an @field, such as "
                + $"type=Topic, 'as_of<2026-04-01' or 'verified.at<@generated.at'; got '{text}'");
        }
        return new FrontmatterFilter(field, op, literal, reference);
    }

    /// <summary>Whether a file meets every one of <paramref name="filters"/>, given its stored frontmatter JSON (null
    /// when it has none) and its parse error.</summary>
    public static bool MatchesAll(IReadOnlyList<FrontmatterFilter> filters, string? frontmatter, string? parseError)
    {
        if (filters.Count == 0)
        {
            return true;
        }
        if (parseError is not null)
        {
            return false;
        }
        if (frontmatter is null)
        {
            return filters.All(filter => filter.Matches(null));
        }
        using var json = JsonDocument.Parse(frontmatter);
        return filters.All(filter => filter.Matches(json.RootElement));
    }

    private bool Matches(JsonElement? frontmatter)
    {
        var values = frontmatter is { } root ? _field.Find(root).ToList() : [];
        return Operator switch
        {
            FrontmatterOperator.Present => values.Any(value => value.ValueKind != JsonValueKind.Null),
            FrontmatterOperator.Missing => values.All(value => value.ValueKind == JsonValueKind.Null),
            FrontmatterOperator.Equal => Pairs(values, frontmatter).Any(pair => IsEqual(pair.Left, pair.Right)),
            FrontmatterOperator.NotEqual => !Pairs(values, frontmatter).Any(pair => IsEqual(pair.Left, pair.Right)),
            _ => Pairs(values, frontmatter).Any(pair => IsInRange(pair.Left, pair.Right)),
        };
    }

    /// <summary>Each scalar of the field against each scalar of the other side: <see cref="Value"/>, or every scalar the
    /// reference reaches.</summary>
    private IEnumerable<(Operand Left, Operand Right)> Pairs(List<JsonElement> values, JsonElement? frontmatter)
    {
        var lefts = values.SelectMany(Elements).Select(Operand.Of);
        if (_reference is null)
        {
            return lefts.Select(left => (left, _value));
        }
        var rights = frontmatter is { } root ? _reference.Find(root).SelectMany(Elements).Select(Operand.Of).ToList() : [];
        return lefts.SelectMany(left => rights.Select(right => (left, right)));
    }

    /// <summary>The scalars a condition is tested against: a list's elements that are not lists or mappings, or the
    /// value itself. A mapping has none.</summary>
    private static IEnumerable<JsonElement> Elements(JsonElement value) => value switch
    {
        { ValueKind: JsonValueKind.Object } => [],
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray()
            .Where(element => element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)),
        _ => [value],
    };

    private static bool IsEqual(Operand left, Operand right) =>
        Operand.CompareNumbers(left, right) is { } order
            ? order == 0
            : string.Equals(left.Text, right.Text, StringComparison.Ordinal);

    private bool IsInRange(Operand left, Operand right)
    {
        int order;
        if (Operand.CompareNumbers(left, right) is { } numberOrder)
        {
            order = numberOrder;
        }
        else if (left.IsOrdered && right.IsOrdered)
        {
            order = string.CompareOrdinal(left.Text, right.Text);
        }
        else
        {
            return false;
        }
        return Operator switch
        {
            FrontmatterOperator.Less => order < 0,
            FrontmatterOperator.LessOrEqual => order <= 0,
            FrontmatterOperator.Greater => order > 0,
            _ => order >= 0,
        };
    }

    /// <summary>One side of a comparison: its text, and its value when it is a number, as an integer too when it is
    /// one, so integers past 2^53 compare exactly. <see cref="IsOrdered"/> is whether a range can compare it, which a
    /// boolean or null cannot.</summary>
    private readonly record struct Operand(string Text, double? Number, long? Integer, bool IsOrdered)
    {
        /// <summary>A literal: a number when it parses as one, and always orderable as text.</summary>
        public static Operand Of(string literal) => new(literal,
            double.TryParse(literal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null,
            long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer : null,
            true);

        /// <summary>A stored scalar: a string as written, and a number, boolean or null as JSON writes it.</summary>
        public static Operand Of(JsonElement scalar) => scalar.ValueKind switch
        {
            JsonValueKind.String => new(scalar.GetString()!, null, null, true),
            JsonValueKind.Number => new(scalar.GetRawText(), scalar.TryGetDouble(out var number) && double.IsFinite(number) ? number : null,
                scalar.TryGetInt64(out var integer) ? integer : null, true),
            _ => new(scalar.GetRawText(), null, null, false),
        };

        /// <summary>How <paramref name="left"/> orders against <paramref name="right"/> as integers when both are, else
        /// as doubles. Null when either is not a number.</summary>
        public static int? CompareNumbers(Operand left, Operand right) =>
            left.Number is not { } leftNumber || right.Number is not { } rightNumber ? null
            : left.Integer is { } leftInteger && right.Integer is { } rightInteger ? leftInteger.CompareTo(rightInteger)
            : leftNumber.CompareTo(rightNumber);
    }
}

/// <summary>The frontmatter fields <c>--field</c> asks for.</summary>
internal static class FrontmatterFields
{
    /// <summary>The paths in every <c>--field</c> value, each a comma-separated list, in the order given and each
    /// once.</summary>
    public static List<string> ParsePaths(IEnumerable<string> values) =>
        values.SelectMany(value => value.Split(',').Select(path => new FrontmatterPath(path).IsValid ? path
                : throw new HippoException(
                    $"--field expects dotted field names separated by commas, such as as_of,verified.at,sources[].id; got '{value}'")))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The value at each of <paramref name="paths"/> in a file's stored frontmatter JSON, keyed by the path as
    /// given: a list or mapping whole, a path through <c>[]</c> as the list of the values it reaches, and a path that
    /// reaches nothing left out. Empty when the file has no frontmatter, or its frontmatter failed to parse.</summary>
    public static Dictionary<string, JsonElement> Read(IReadOnlyList<string> paths, string? frontmatter)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (frontmatter is null)
        {
            return fields;
        }
        using var json = JsonDocument.Parse(frontmatter);
        foreach (var text in paths)
        {
            var path = new FrontmatterPath(text);
            var values = path.Find(json.RootElement).ToList();
            if (values.Count == 0)
            {
                continue;
            }
            fields[text] = path.IsEach ? List(values) : values[0].Clone();
        }
        return fields;
    }

    /// <summary><paramref name="values"/> as one JSON list.</summary>
    private static JsonElement List(List<JsonElement> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                value.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        using var list = JsonDocument.Parse(buffer.WrittenMemory);
        return list.RootElement.Clone();
    }
}
