using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hippo.Commands;

internal sealed record IndexOutput(int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt, long ElapsedMs);

internal sealed record StatusOutput(string Root, string Database, CountsOutput Files, SweepOutput LastSweep);

internal sealed record CountsOutput(long Total, long Markdown, long Other, long ParseErrors);

internal sealed record SweepOutput(DateTimeOffset FinishedAt, long ElapsedMs, int Added, int Updated, int Removed);

/// <summary>A file <c>find</c> lists. <see cref="Title"/> is null when the file is not a page or the page has none;
/// <see cref="Snippet"/> is null without a query, and with one marks each matched term <c>**</c>. <see cref="Fields"/>
/// holds the frontmatter fields <c>--field</c> asks for, keyed as asked, and is left out without it.</summary>
internal sealed record FindOutput(
    string Path, string Kind, long Size, DateTimeOffset Modified, string? Title, string? ParseError, string? Snippet,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, JsonElement>? Fields = null);

internal sealed record ShowOutput(
    string Path, string Kind, long Size, DateTimeOffset Modified, string Hash, JsonElement? Frontmatter, string? ParseError);

/// <summary>A link out of a file. <see cref="Text"/> is what a reader sees as a body link, as plain text; null for a
/// frontmatter link.</summary>
internal sealed record RefOutput(long Line, string Kind, string Type, string Raw, string? Target, string? Text);

/// <summary>A link into a path. <see cref="Text"/> is as for <see cref="RefOutput"/>.</summary>
internal sealed record BackrefOutput(string Source, long Line, string Kind, string Raw, string? Text);

internal sealed record TransitiveBackrefOutput(string Source);

internal sealed record FindingOutput(string Rule, string Path, long? Line, string Message, List<string> Related);

internal sealed record CacheIndexOutput(string Database, string? Root, string State, long Size);

/// <summary>
/// The <c>--json</c> shapes. They are hippo's contract with scripts, so each is a record of its own, never a row type
/// from <c>Hippo.Indexing</c>, and <c>OutputJsonTests</c> pins every one. Generated, so serialization needs no
/// reflection under native AOT.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IndexOutput))]
[JsonSerializable(typeof(StatusOutput))]
[JsonSerializable(typeof(List<FindOutput>))]
[JsonSerializable(typeof(ShowOutput))]
[JsonSerializable(typeof(List<RefOutput>))]
[JsonSerializable(typeof(List<BackrefOutput>))]
[JsonSerializable(typeof(List<TransitiveBackrefOutput>))]
[JsonSerializable(typeof(List<FindingOutput>))]
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

    /// <summary>A link's text as it ends a line of <c>refs</c> or <c>backrefs</c>: in brackets, as markdown shows a
    /// link's label, so an empty label reads <c>[]</c>; nothing for a frontmatter link, which has no text.</summary>
    public static string LinkText(string? text) => text is null ? "" : $"  [{Safe(text)}]";

    /// <summary>
    /// Lays out <paramref name="json"/> as <c>--json</c> does, but with strings as written: only the quote, the
    /// backslash and control characters are escaped, the last for the reason <see cref="Safe"/> gives. The serializer
    /// would also escape apostrophes, <c>&lt;</c>, <c>&amp;</c> and all non-ASCII, which no encoder it offers stops
    /// for emoji.
    /// </summary>
    public static string Indented(JsonElement json)
    {
        var text = new StringBuilder();
        WriteJson(text, json, 0);
        return text.ToString();
    }

    /// <summary>Writes <paramref name="json"/> on one line with no spaces, its strings as <see cref="Indented"/> writes
    /// them.</summary>
    public static string Compact(JsonElement json)
    {
        var text = new StringBuilder();
        WriteJson(text, json, null);
        return text.ToString();
    }

    /// <summary>Writes <paramref name="json"/> indented as at <paramref name="depth"/>, or compact when it is
    /// null.</summary>
    private static void WriteJson(StringBuilder text, JsonElement json, int? depth)
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
        StringBuilder text, char open, char close, IEnumerable<(string? Name, JsonElement Value)> items, int? depth)
    {
        text.Append(open);
        var any = false;
        foreach (var (name, value) in items)
        {
            text.Append(any ? "," : "");
            if (depth is { } indent)
            {
                text.Append(Environment.NewLine).Append(' ', 2 * (indent + 1));
            }
            if (name is not null)
            {
                WriteString(text, name);
                text.Append(depth is null ? ":" : ": ");
            }
            WriteJson(text, value, depth + 1);
            any = true;
        }
        if (any && depth is { } outer)
        {
            text.Append(Environment.NewLine).Append(' ', 2 * outer);
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
