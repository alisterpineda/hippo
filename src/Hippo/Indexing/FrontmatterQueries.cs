using System.Globalization;
using System.Text.Json;

namespace Hippo.Indexing;

/// <summary>A dotted path into a file's frontmatter, as <c>--where</c> and <c>--field</c> take it: each part a key of
/// the mapping the part before it names. There is no wildcard, and <c>[</c> and <c>]</c> are refused rather than read
/// as the config's <c>[]</c>, so a key holding <c>.</c>, <c>[</c> or <c>]</c> cannot be reached.</summary>
internal static class FrontmatterPath
{
    /// <summary>Whether <paramref name="path"/> has no empty part and no <c>[</c> or <c>]</c>.</summary>
    public static bool IsValid(string path) => path.Split('.').All(part => part.Length > 0 && part.IndexOfAny(['[', ']']) < 0);

    /// <summary>The value at <paramref name="path"/> in <paramref name="frontmatter"/>, null when a key on the way is
    /// missing or names something other than a mapping.</summary>
    public static JsonElement? Find(JsonElement frontmatter, string path)
    {
        var value = frontmatter;
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                return null;
            }
        }
        return value;
    }
}

internal enum FrontmatterOperator { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, Present, Missing }

/// <summary>
/// One <c>--where</c> condition on a frontmatter <see cref="Field"/>. A number is compared as a number when
/// <see cref="Value"/> parses as one, and anything else as ordinal text, so ISO-8601 dates order correctly. A list
/// matches when any element does. <see cref="FrontmatterOperator.NotEqual"/> and <see cref="FrontmatterOperator.Missing"/>
/// are the exact negations of <see cref="FrontmatterOperator.Equal"/> and <see cref="FrontmatterOperator.Present"/>,
/// so a file without the field meets them, but frontmatter that failed to parse meets no condition at all.
/// </summary>
internal sealed record FrontmatterFilter(string Field, FrontmatterOperator Operator, string Value)
{
    private static readonly char[] OperatorStarts = ['=', '<', '>', '!'];

    /// <summary><see cref="Value"/> as a number, null when it is not one.</summary>
    private readonly double? _number =
        double.TryParse(Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary><see cref="Value"/> as an integer, null when it is not one, so integers past 2^53 compare exactly.</summary>
    private readonly long? _integer =
        long.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer : null;

    /// <summary>Parses <c>field</c>, <c>!field</c>, or a field, an operator (<c>=</c>, <c>!=</c>, <c>&lt;</c>,
    /// <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) and a value. The field ends at the first character that can start an
    /// operator, so it can hold none of them; the value can hold anything.</summary>
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
        if (!FrontmatterPath.IsValid(field))
        {
            throw new HippoException(
                "--where expects <field>, !<field>, or <field> then =, !=, <, <=, > or >= and a value, such as type=Topic "
                + $"or 'as_of<2026-04-01'; got '{text}'");
        }
        return new FrontmatterFilter(field, op, value);
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
        var value = frontmatter is { } root ? FrontmatterPath.Find(root, Field) : null;
        return Operator switch
        {
            FrontmatterOperator.Present => value is { ValueKind: not JsonValueKind.Null },
            FrontmatterOperator.Missing => value is null or { ValueKind: JsonValueKind.Null },
            FrontmatterOperator.Equal => Elements(value).Any(IsEqual),
            FrontmatterOperator.NotEqual => !Elements(value).Any(IsEqual),
            _ => Elements(value).Any(IsInRange),
        };
    }

    /// <summary>The scalars a condition is tested against: a list's elements that are not lists or mappings, or the
    /// value itself. A mapping has none.</summary>
    private static IEnumerable<JsonElement> Elements(JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Object } => [],
        { ValueKind: JsonValueKind.Array } list => list.EnumerateArray()
            .Where(element => element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)),
        { } scalar => [scalar],
    };

    /// <summary>How a stored number orders against <see cref="Value"/>: as integers when both are, else as doubles. Null
    /// when either is not a number.</summary>
    private int? CompareNumber(JsonElement element) =>
        element.ValueKind != JsonValueKind.Number || _number is not { } number ? null
        : _integer is { } integer && element.TryGetInt64(out var stored) ? stored.CompareTo(integer)
        : element.GetDouble().CompareTo(number);

    private bool IsEqual(JsonElement element) =>
        CompareNumber(element) is { } order
            ? order == 0
            : string.Equals(Text(element), Value, StringComparison.Ordinal);

    private bool IsInRange(JsonElement element)
    {
        int order;
        if (CompareNumber(element) is { } numberOrder)
        {
            order = numberOrder;
        }
        else if (element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            order = string.CompareOrdinal(Text(element), Value);
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

    /// <summary>A scalar as text: a string as written, and a number, boolean or null as JSON writes it.</summary>
    private static string Text(JsonElement scalar) =>
        scalar.ValueKind == JsonValueKind.String ? scalar.GetString()! : scalar.GetRawText();
}

/// <summary>The frontmatter fields <c>--field</c> asks for.</summary>
internal static class FrontmatterFields
{
    /// <summary>The paths in every <c>--field</c> value, each a comma-separated list, in the order given and each
    /// once.</summary>
    public static List<string> ParsePaths(IEnumerable<string> values) =>
        values.SelectMany(value => value.Split(',').Select(path => FrontmatterPath.IsValid(path) ? path
                : throw new HippoException(
                    $"--field expects dotted field names separated by commas, such as as_of,verified.at; got '{value}'")))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The value at each of <paramref name="paths"/> in a file's stored frontmatter JSON, keyed by the path as
    /// given: a list or mapping whole, and a missing field left out. Empty when the file has no frontmatter, or its
    /// frontmatter failed to parse.</summary>
    public static Dictionary<string, JsonElement> Read(IReadOnlyList<string> paths, string? frontmatter)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (frontmatter is null)
        {
            return fields;
        }
        using var json = JsonDocument.Parse(frontmatter);
        foreach (var path in paths)
        {
            if (FrontmatterPath.Find(json.RootElement, path) is { } value)
            {
                fields[path] = value.Clone();
            }
        }
        return fields;
    }
}
