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

/// <summary>A markdown file split at its frontmatter: the parsed <see cref="Result"/>, the YAML mapping behind it when it
/// parsed cleanly, and the <see cref="Body"/> after the closing line, which starts on file line <see cref="BodyLine"/>.
/// A file without a closed block is all body.</summary>
internal sealed record FrontmatterBlock(FrontmatterResult Result, YamlMappingNode? Root, string Body, int BodyLine);

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

    public static FrontmatterResult Parse(string text) => Read(text).Result;

    public static FrontmatterBlock Read(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var lines = new LineReader(text);
        if (!lines.TryNext(out var first) || !IsDelimiter(first))
        {
            return new(default, null, text, 1);
        }

        var yamlStart = lines.Position;
        var yamlEnd = -1;
        var closingLine = 1;
        while (lines.TryNext(out var line))
        {
            closingLine++;
            if (IsDelimiter(line))
            {
                yamlEnd = lines.LineStart;
                break;
            }
        }
        if (yamlEnd < 0)
        {
            return new(new(null, "frontmatter opened on line 1 is never closed"), null, text, 1);
        }

        var body = text[lines.Position..];
        try
        {
            var (json, root) = ToJson(text[yamlStart..yamlEnd]);
            return new(new(json, null), root, body, closingLine + 1);
        }
        catch (YamlException ex)
        {
            return new(new(null, $"line {FileLine(ex.Start)}: {PositionPrefix().Replace(ex.Message, "")}"), null, body, closingLine + 1);
        }
        catch (FrontmatterException ex)
        {
            return new(new(null, ex.Message), null, body, closingLine + 1);
        }
    }

    private static bool IsDelimiter(ReadOnlySpan<char> line) => line.TrimEnd() is "---";

    /// <summary>The file line of a position in the YAML, which starts on the file's second line.</summary>
    public static int FileLine(Mark mark) => (int)mark.Line + 1;

    /// <summary>Whether a scalar is a string by its style or tag, whatever its text.</summary>
    private static bool IsString(YamlScalarNode scalar) =>
        scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)
        || scalar.Tag is { IsEmpty: false } tag && tag.Value is "tag:yaml.org,2002:str" or "!";

    /// <summary>Whether a scalar is YAML null by the core schema: an untagged plain empty, <c>~</c> or <c>null</c>.</summary>
    public static bool IsNull(YamlScalarNode scalar) =>
        !IsString(scalar) && (scalar.Value ?? "") is "" or "~" or "null" or "Null" or "NULL";

    private static (string Json, YamlMappingNode? Root) ToJson(string yaml)
    {
        var stream = new YamlStream();
        // Root mapping included, the same number of levels Write accepts.
        stream.Load(new DepthLimitedParser(new Parser(new StringReader(yaml)), MaxDepth + 1));

        YamlMappingNode? root = null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (stream.Documents.Count == 0)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else if (stream.Documents[0].RootNode is YamlMappingNode mapping)
            {
                var nodes = 0;
                Write(writer, mapping, 0, ref nodes);
                root = mapping;
            }
            else
            {
                throw new FrontmatterException("frontmatter is not a mapping");
            }
        }
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), root);
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
                        throw new FrontmatterException($"line {FileLine(key.Start)}: keys must be plain values");
                    }
                    if (!keys.Add(name))
                    {
                        throw new FrontmatterException($"line {FileLine(key.Start)}: duplicate key '{name}'");
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
                throw new FrontmatterException($"line {FileLine(node.Start)}: unresolved alias");
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (IsString(scalar))
        {
            writer.WriteStringValue(value);
        }
        else if (IsNull(scalar))
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
