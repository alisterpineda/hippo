using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hippo.Commands;

internal sealed record IndexOutput(int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt, long ElapsedMs);

internal sealed record StatusOutput(string Root, string Database, CountsOutput Files, SweepOutput LastSweep);

internal sealed record CountsOutput(long Total, long Markdown, long Other, long ParseErrors);

internal sealed record SweepOutput(DateTimeOffset FinishedAt, long ElapsedMs, int Added, int Updated, int Removed);

internal sealed record FileOutput(string Path, string Kind, long Size, DateTimeOffset Modified, string? ParseError);

internal sealed record ShowOutput(
    string Path, string Kind, long Size, DateTimeOffset Modified, string Hash, JsonElement? Frontmatter, string? ParseError);

internal sealed record RefOutput(long Line, string Kind, string Type, string Raw, string? Target);

internal sealed record BackrefOutput(string Source, long Line, string Kind, string Raw);

internal sealed record TransitiveBackrefOutput(string Source);

internal sealed record BrokenOutput(string Source, long Line, string Kind, string Raw, string? Target);

internal sealed record OrphanOutput(string Path);

internal sealed record CacheIndexOutput(string Database, string? Root, string State, long Size);

/// <summary>
/// The <c>--json</c> shapes. They are hippo's contract with scripts, so each is a record of its own, never a row type
/// from <c>Hippo.Indexing</c>, and <c>OutputJsonTests</c> pins every one. Generated, so serialization needs no
/// reflection under native AOT.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IndexOutput))]
[JsonSerializable(typeof(StatusOutput))]
[JsonSerializable(typeof(List<FileOutput>))]
[JsonSerializable(typeof(ShowOutput))]
[JsonSerializable(typeof(List<RefOutput>))]
[JsonSerializable(typeof(List<BackrefOutput>))]
[JsonSerializable(typeof(List<TransitiveBackrefOutput>))]
[JsonSerializable(typeof(List<BrokenOutput>))]
[JsonSerializable(typeof(List<OrphanOutput>))]
[JsonSerializable(typeof(List<CacheIndexOutput>))]
internal sealed partial class OutputJson : JsonSerializerContext;

/// <summary>How values read as text. Named apart from the <c>*Output</c> shapes and the session's output writer.</summary>
internal static class Format
{
    /// <summary>Converts a stored mtime (100 ns ticks since the Unix epoch) to a timestamp.</summary>
    public static DateTimeOffset Modified(long mtime) => DateTimeOffset.UnixEpoch.AddTicks(mtime);

    public static long Milliseconds(TimeSpan elapsed) => (long)elapsed.TotalMilliseconds;

    public static string Summary(int added, int updated, int removed) =>
        $"{added} added, {updated} updated, {removed} removed";

    /// <summary>
    /// Replaces control characters other than tab with visible <c>\xNN</c> escapes. File names, YAML keys and the
    /// messages that quote them come from the workspace, and printed raw they could drive the terminal (retitle it,
    /// write the clipboard, rewrite earlier output).
    /// </summary>
    public static string Safe(string text)
    {
        if (!text.Any(IsUnsafe))
        {
            return text;
        }
        var safe = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (IsUnsafe(c))
            {
                safe.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
            }
            else
            {
                safe.Append(c);
            }
        }
        return safe.ToString();
    }

    /// <summary>Escapes each line of <paramref name="text"/> as <see cref="Safe"/> does, keeping the CR and LF line
    /// breaks. Form feed and NEL are escaped like any other control character, not taken as breaks.</summary>
    public static string SafeLines(string text) =>
        string.Join(Environment.NewLine, text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).Select(Safe));

    private static bool IsUnsafe(char c) => char.IsControl(c) && c != '\t';

    /// <summary>
    /// Lays out <paramref name="json"/> as <c>--json</c> does, but with strings as written: only the quote, the
    /// backslash and control characters are escaped, the last for the reason <see cref="Safe"/> gives. The serializer
    /// would also escape apostrophes, <c>&lt;</c>, <c>&amp;</c> and all non-ASCII, which no encoder it offers stops
    /// for emoji.
    /// </summary>
    public static string Indented(JsonElement json)
    {
        var text = new StringBuilder();
        WriteIndented(text, json, 0);
        return text.ToString();
    }

    private static void WriteIndented(StringBuilder text, JsonElement json, int depth)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                WriteItems(text, '{', '}', json.EnumerateObject().Select(p => ((string?)p.Name, p.Value)), depth);
                break;
            case JsonValueKind.Array:
                WriteItems(text, '[', ']', json.EnumerateArray().Select(v => ((string?)null, v)), depth);
                break;
            case JsonValueKind.String:
                WriteString(text, json.GetString()!);
                break;
            default:
                text.Append(json.GetRawText());
                break;
        }
    }

    private static void WriteItems(
        StringBuilder text, char open, char close, IEnumerable<(string? Name, JsonElement Value)> items, int depth)
    {
        text.Append(open);
        var any = false;
        foreach (var (name, value) in items)
        {
            text.Append(any ? "," : "").Append(Environment.NewLine).Append(' ', 2 * (depth + 1));
            if (name is not null)
            {
                WriteString(text, name);
                text.Append(": ");
            }
            WriteIndented(text, value, depth + 1);
            any = true;
        }
        if (any)
        {
            text.Append(Environment.NewLine).Append(' ', 2 * depth);
        }
        text.Append(close);
    }

    private static void WriteString(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case var _ when IsUnsafe(c):
                    text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
}
