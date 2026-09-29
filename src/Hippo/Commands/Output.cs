using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hippo.Indexing;

namespace Hippo.Commands;

internal sealed record IndexOutput(int Files, int Added, int Updated, int Removed, int Hashed, bool Rebuilt, long ElapsedMs);

internal sealed record StatusOutput(string Root, string Database, CountsOutput Files, SweepOutput LastSweep);

internal sealed record CountsOutput(long Total, long Markdown, long Plain, long ParseErrors);

internal sealed record SweepOutput(DateTimeOffset FinishedAt, long ElapsedMs, int Added, int Updated, int Removed);

internal sealed record FileOutput(string Path, string Kind, long Size, DateTimeOffset Modified);

internal sealed record ShowOutput(
    string Path, string Kind, long Size, DateTimeOffset Modified, string Hash, JsonElement? Frontmatter, string? ParseError);

internal sealed record TransitiveBackrefOutput(string Source);

internal sealed record OrphanOutput(string Path);

/// <summary>The <c>--json</c> shapes. Generated, so serialization needs no reflection under native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(IndexOutput))]
[JsonSerializable(typeof(StatusOutput))]
[JsonSerializable(typeof(List<FileOutput>))]
[JsonSerializable(typeof(ShowOutput))]
[JsonSerializable(typeof(List<LinkOut>))]
[JsonSerializable(typeof(List<LinkIn>))]
[JsonSerializable(typeof(List<TransitiveBackrefOutput>))]
[JsonSerializable(typeof(List<BrokenLink>))]
[JsonSerializable(typeof(List<OrphanOutput>))]
internal sealed partial class OutputJson : JsonSerializerContext;

/// <summary>How values read as text. Named apart from the <c>*Output</c> shapes and the session's output writer.</summary>
internal static class Format
{
    /// <summary>Converts a stored mtime (100 ns ticks since the Unix epoch) to a timestamp.</summary>
    public static DateTimeOffset Modified(long mtime) => DateTimeOffset.UnixEpoch.AddTicks(mtime);

    public static long Milliseconds(TimeSpan elapsed) => (long)elapsed.TotalMilliseconds;

    public static string Summary(SweepResult sweep) =>
        $"{sweep.Added} added, {sweep.Updated} updated, {sweep.Removed} removed";

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

    private static bool IsUnsafe(char c) => char.IsControl(c) && c != '\t';

    public static string Indented(JsonElement json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
