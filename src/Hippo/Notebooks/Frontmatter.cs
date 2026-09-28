using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hippo.Notebooks;

/// <summary>The frontmatter of a markdown file as a JSON object, or why it could not be read. Both are null when the
/// file has no frontmatter.</summary>
internal readonly record struct FrontmatterResult(string? Json, string? Error);

/// <summary>
/// Reads the YAML block that opens a markdown file between two <c>---</c> lines and converts it to JSON. Plain scalars
/// resolve by the YAML 1.2 core schema (null, booleans, integers, floats); everything else stays a string. A malformed
/// block is reported as an error, never thrown.
/// </summary>
internal static partial class Frontmatter
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 100_000;

    public static FrontmatterResult Parse(ReadOnlySpan<byte> utf8) => Parse(Encoding.UTF8.GetString(utf8));

    public static FrontmatterResult Parse(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var lines = new LineReader(text);
        if (!lines.TryNext(out var first) || !IsDelimiter(first))
        {
            return default;
        }

        var yamlStart = lines.Position;
        var yamlEnd = -1;
        while (lines.TryNext(out var line))
        {
            if (IsDelimiter(line))
            {
                yamlEnd = lines.LineStart;
                break;
            }
        }
        if (yamlEnd < 0)
        {
            return new(null, "frontmatter opened on line 1 is never closed");
        }

        try
        {
            return new(ToJson(text[yamlStart..yamlEnd]), null);
        }
        catch (YamlException ex)
        {
            // The YAML starts on the file's second line.
            return new(null, $"line {ex.Start.Line + 1}: {PositionPrefix().Replace(ex.Message, "")}");
        }
        catch (FrontmatterException ex)
        {
            return new(null, ex.Message);
        }
    }

    private static bool IsDelimiter(ReadOnlySpan<char> line) => line.TrimEnd() is "---";

    private static string ToJson(string yaml)
    {
        var stream = new YamlStream();
        // Root mapping included, the same number of levels Write accepts.
        stream.Load(new DepthLimitedParser(new Parser(new StringReader(yaml)), MaxDepth + 1));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (stream.Documents.Count == 0)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else if (stream.Documents[0].RootNode is YamlMappingNode root)
            {
                var nodes = 0;
                Write(writer, root, 0, ref nodes);
            }
            else
            {
                throw new FrontmatterException("frontmatter is not a mapping");
            }
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node, int depth, ref int nodes)
    {
        if (depth > MaxDepth || ++nodes > MaxNodes)
        {
            throw new FrontmatterException("frontmatter is nested too deeply or expands too far");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        throw new FrontmatterException($"line {key.Start.Line + 1}: keys must be plain values");
                    }
                    if (!keys.Add(name))
                    {
                        throw new FrontmatterException($"line {key.Start.Line + 1}: duplicate key '{name}'");
                    }
                    writer.WritePropertyName(name);
                    Write(writer, value, depth + 1, ref nodes);
                }
                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    Write(writer, item, depth + 1, ref nodes);
                }
                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteScalar(writer, scalar);
                break;
            default:
                throw new FrontmatterException($"line {node.Start.Line + 1}: unresolved alias");
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var isString = scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)
            || scalar.Tag is { IsEmpty: false } tag && tag.Value is "tag:yaml.org,2002:str" or "!";
        if (isString)
        {
            writer.WriteStringValue(value);
        }
        else if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            writer.WriteNullValue();
        }
        else if (value is "true" or "True" or "TRUE")
        {
            writer.WriteBooleanValue(true);
        }
        else if (value is "false" or "False" or "FALSE")
        {
            writer.WriteBooleanValue(false);
        }
        else if (DecimalInteger().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            writer.WriteNumberValue(integer);
        }
        else if (value.StartsWith("0o", StringComparison.Ordinal) && OctalDigits().IsMatch(value.AsSpan(2)) && TryParseOctal(value.AsSpan(2), out var octal))
        {
            writer.WriteNumberValue(octal);
        }
        else if (value.StartsWith("0x", StringComparison.Ordinal) && long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) && hex >= 0)
        {
            writer.WriteNumberValue(hex);
        }
        else if (Float().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
        {
            writer.WriteNumberValue(real);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static bool TryParseOctal(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            if (value > (long.MaxValue >> 3))
            {
                return false;
            }
            value = (value << 3) | (long)(digit - '0');
        }
        return true;
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex(@"^[0-7]+$")]
    private static partial Regex OctalDigits();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    [GeneratedRegex(@"^\(Line: .*?\) - \(Line: .*?\): ")]
    private static partial Regex PositionPrefix();

    private sealed class FrontmatterException(string message) : Exception(message);

    /// <summary>Walks lines, tracking where each starts so the YAML between the delimiters can be sliced out.</summary>
    private ref struct LineReader(string text)
    {
        private readonly string _text = text;

        public int Position { get; private set; }

        public int LineStart { get; private set; }

        public bool TryNext(out ReadOnlySpan<char> line)
        {
            if (Position >= _text.Length)
            {
                line = default;
                return false;
            }
            LineStart = Position;
            var end = _text.IndexOf('\n', Position);
            if (end < 0)
            {
                end = _text.Length;
                Position = end;
            }
            else
            {
                Position = end + 1;
            }
            line = _text.AsSpan(LineStart, end - LineStart).TrimEnd('\r');
            return true;
        }
    }
}
