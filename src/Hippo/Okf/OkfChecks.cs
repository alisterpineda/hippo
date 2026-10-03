using System.Globalization;
using System.Text.RegularExpressions;
using Hippo.Workspaces;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.RepresentationModel;

namespace Hippo.Okf;

/// <summary>
/// Checks one markdown file in an OKF bundle against OKF v0.2. Every rule looks at the file alone, so its findings are
/// derived when the file is parsed and kept beside its links. <c>index.md</c> and <c>log.md</c> are reserved at every
/// level (§3.1): they are checked for their own structure (§8, §9) and every other file as a concept.
/// </summary>
internal static partial class OkfChecks
{
    /// <summary>Checks the page at <paramref name="path"/> in the bundle at <paramref name="bundle"/>.
    /// <paramref name="document"/> is its body as CommonMark with footnotes, or null when Markdig could not parse it, in
    /// which case only its frontmatter is checked.</summary>
    public static List<Finding> Check(string path, string bundle, FrontmatterBlock block, MarkdownDocument? document)
    {
        var findings = new List<Finding>();
        switch (path[(path.LastIndexOf('/') + 1)..])
        {
            case OkfBundle.IndexName:
                CheckIndex(findings, path == OkfBundle.IndexOf(bundle), block);
                break;
            case OkfBundle.LogName:
                if (document is not null)
                {
                    CheckLog(findings, document, block.BodyLine);
                }
                break;
            default:
                CheckConcept(findings, block, document);
                break;
        }
        return findings;
    }

    /// <summary>§8: an index has no frontmatter, but the bundle root's may declare <c>okf_version</c> (§12).</summary>
    private static void CheckIndex(List<Finding> findings, bool atRoot, FrontmatterBlock block)
    {
        if (!atRoot)
        {
            if (block.Result.Json is not null || block.Result.Error is not null)
            {
                findings.Add(new(OkfRules.IndexFrontmatter, 1, "an index.md below the bundle root has frontmatter"));
            }
            return;
        }
        foreach (var key in block.Root?.Children.Keys ?? [])
        {
            if (key is YamlScalarNode { Value: { } name } && name != OkfBundle.VersionKey)
            {
                findings.Add(new(OkfRules.IndexFrontmatter, Line(key),
                    $"the bundle root's index.md may hold only {OkfBundle.VersionKey} in its frontmatter, not {name}"));
            }
        }
    }

    /// <summary>§9: each date heading is <c>YYYY-MM-DD</c>. The dates group a flat list of entries under the log's
    /// title, so every level-2 heading at the top level is a date heading.</summary>
    private static void CheckLog(List<Finding> findings, MarkdownDocument document, int bodyLine)
    {
        foreach (var heading in document.OfType<HeadingBlock>().Where(heading => heading.Level == 2))
        {
            var text = PlainText.Of(heading.Inline).Trim();
            if (!IsDate(text))
            {
                findings.Add(new(OkfRules.LogDate, heading.Line + bodyLine, $"date heading '{text}' is not a YYYY-MM-DD date"));
            }
        }
    }

    private static void CheckConcept(List<Finding> findings, FrontmatterBlock block, MarkdownDocument? document)
    {
        var result = block.Result;
        if (result.Error is not null)
        {
            // Without its frontmatter, nothing else about the page can be checked: not even its footnotes, as the
            // sources they should name are unknown.
            findings.Add(new(OkfRules.Type, null, $"its frontmatter cannot be parsed: {result.Error}"));
            return;
        }
        if (result.Json is null)
        {
            findings.Add(new(OkfRules.Type, null, "it has no frontmatter"));
            return;
        }

        // An empty block parses to no mapping at all.
        var root = block.Root ?? new YamlMappingNode();
        CheckType(findings, root);
        CheckSourceResources(findings, root);
        CheckTimestamps(findings, root);
        CheckActors(findings, root);
        CheckStatus(findings, root);
        if (document is not null)
        {
            CheckFootnotes(findings, root, document, block.BodyLine);
        }
    }

    /// <summary>§4.1, §11: a non-empty <c>type</c>.</summary>
    private static void CheckType(List<Finding> findings, YamlMappingNode root)
    {
        if (Frontmatter.Field(root, "type") is not (var key, var value))
        {
            findings.Add(new(OkfRules.Type, null, "its frontmatter has no type"));
        }
        else if (value is not YamlScalarNode { Value: { } type } || Frontmatter.IsNull(value) || string.IsNullOrWhiteSpace(type))
        {
            findings.Add(new(OkfRules.Type, Line(key), "type is empty"));
        }
    }

    /// <summary>§5.1: <c>resource</c> is required within a <c>sources</c> entry.</summary>
    private static void CheckSourceResources(List<Finding> findings, YamlMappingNode root)
    {
        foreach (var entry in Sources(root))
        {
            if (entry is not YamlMappingNode source || Frontmatter.Field(source, "resource") is not { Value: YamlScalarNode { Value.Length: > 0 } resource }
                || Frontmatter.IsNull(resource))
            {
                findings.Add(new(OkfRules.SourceResource, Line(entry), "sources entry has no resource"));
            }
        }
    }

    /// <summary>§5: every timestamp is an ISO 8601 datetime with an explicit UTC offset.</summary>
    private static void CheckTimestamps(List<Finding> findings, YamlMappingNode root)
    {
        var timestamps = new List<(string Field, YamlNode Value)>();
        if (Frontmatter.Field(root, "generated") is { Value: YamlMappingNode generated })
        {
            AddField(timestamps, "generated.at", generated, "at");
        }
        foreach (var verified in Verified(root))
        {
            AddField(timestamps, "verified[].at", verified, "at");
        }
        AddField(timestamps, "stale_after", root, "stale_after");
        foreach (var source in Sources(root).OfType<YamlMappingNode>())
        {
            AddField(timestamps, "sources[].last_modified", source, "last_modified");
            AddWindow(timestamps, "sources[].usage_window", source);
        }
        AddWindow(timestamps, "usage_window", root);

        foreach (var (field, value) in timestamps)
        {
            var text = value is YamlScalarNode { Value: { } scalar } ? scalar : null;
            var problem = text is null ? "is not a datetime" : TimestampProblem(text);
            if (problem is not null)
            {
                findings.Add(new(OkfRules.Timestamp, Line(value), text is null ? $"{field} {problem}" : $"{field} '{text}' {problem}"));
            }
        }
    }

    /// <summary>§5.2, §7: <c>generated</c> has a <c>by</c>, and each actor is in the actor convention.
    /// <c>sources[].author</c> is left alone: the spec's own example uses a <c>team:</c> prefix §7 does not define.</summary>
    private static void CheckActors(List<Finding> findings, YamlMappingNode root)
    {
        if (Frontmatter.Field(root, "generated") is (var key, var value) && !Frontmatter.IsNull(value))
        {
            if (value is not YamlMappingNode generated || Frontmatter.Field(generated, "by") is not { } by || Frontmatter.IsNull(by.Value))
            {
                findings.Add(new(OkfRules.Actor, Line(key), "generated has no by"));
            }
            else
            {
                CheckActor(findings, "generated.by", by.Value);
            }
        }
        foreach (var verified in Verified(root))
        {
            if (Frontmatter.Field(verified, "by") is { } by && !Frontmatter.IsNull(by.Value))
            {
                CheckActor(findings, "verified[].by", by.Value);
            }
        }
    }

    private static void CheckActor(List<Finding> findings, string field, YamlNode value)
    {
        if (value is not YamlScalarNode { Value: { } actor })
        {
            findings.Add(new(OkfRules.Actor, Line(value), $"{field} is not an actor"));
        }
        else if (!Actor().IsMatch(actor))
        {
            findings.Add(new(OkfRules.Actor, Line(value), $"{field} '{actor}' is not <producer>/<version>, human:<id> or process:<id>"));
        }
    }

    /// <summary>§5.4: <c>status</c> is <c>draft</c>, <c>stable</c> or <c>deprecated</c>.</summary>
    private static void CheckStatus(List<Finding> findings, YamlMappingNode root)
    {
        if (Frontmatter.Field(root, "status") is not { Value: var value } || Frontmatter.IsNull(value))
        {
            return;
        }
        if (value is not YamlScalarNode { Value: "draft" or "stable" or "deprecated" })
        {
            var text = value is YamlScalarNode { Value: { } status } ? $" '{status}'" : "";
            findings.Add(new(OkfRules.Status, Line(value), $"status{text} is not draft, stable or deprecated"));
        }
    }

    /// <summary>§5.1: a footnote attributes a claim by taking a <c>sources[].id</c> as its label. An explanatory
    /// footnote is reported too, since nothing tells it from a citation. A definition nothing references attributes
    /// nothing, and Markdig leaves it out of the document.</summary>
    private static void CheckFootnotes(List<Finding> findings, YamlMappingNode root, MarkdownDocument document, int bodyLine)
    {
        var ids = Sources(root)
            .OfType<YamlMappingNode>()
            .Select(source => Frontmatter.Field(source, "id")?.Value)
            .OfType<YamlScalarNode>()
            .Where(id => !Frontmatter.IsNull(id))
            .Select(id => id.Value!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var footnote in document.Descendants<Footnote>())
        {
            // Markdig keeps the caret in the label.
            var label = footnote.Label?.TrimStart('^') ?? "";
            if (!ids.Contains(label))
            {
                findings.Add(new(OkfRules.Footnote, footnote.Line + bodyLine, $"footnote [^{label}] matches no sources[].id"));
            }
        }
    }

    /// <summary>The entries of <c>sources</c>, when it is a list.</summary>
    private static IEnumerable<YamlNode> Sources(YamlMappingNode root) =>
        Frontmatter.Field(root, "sources") is { Value: YamlSequenceNode sources } ? sources.Children : [];

    /// <summary>The entries of <c>verified</c>, a bare mapping being a one-element list (§5.2).</summary>
    private static IEnumerable<YamlMappingNode> Verified(YamlMappingNode root) => Frontmatter.Field(root, "verified")?.Value switch
    {
        YamlMappingNode single => [single],
        YamlSequenceNode list => list.Children.OfType<YamlMappingNode>(),
        _ => [],
    };

    /// <summary>Adds <paramref name="mapping"/>'s field <paramref name="name"/> as <paramref name="label"/> when it has
    /// a value.</summary>
    private static void AddField(List<(string, YamlNode)> fields, string label, YamlMappingNode mapping, string name)
    {
        if (Frontmatter.Field(mapping, name) is { Value: var value } && !Frontmatter.IsNull(value))
        {
            fields.Add((label, value));
        }
    }

    /// <summary>Adds the <c>from</c> and <c>to</c> of <paramref name="mapping"/>'s <c>usage_window</c>.</summary>
    private static void AddWindow(List<(string, YamlNode)> fields, string label, YamlMappingNode mapping)
    {
        if (Frontmatter.Field(mapping, "usage_window") is { Value: YamlMappingNode window })
        {
            AddField(fields, $"{label}.from", window, "from");
            AddField(fields, $"{label}.to", window, "to");
        }
    }

    /// <summary>Why <paramref name="text"/> is not an ISO 8601 datetime with an explicit UTC offset, or null when it
    /// is one.</summary>
    private static string? TimestampProblem(string text)
    {
        var match = IsoDateTime().Match(text);
        if (!match.Success || !IsDate(match.Groups["date"].Value))
        {
            return "is not an ISO 8601 datetime with a UTC offset";
        }
        return match.Groups["offset"].Success ? null : "has no UTC offset";
    }

    private static bool IsDate(string text) =>
        Date().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static int Line(YamlNode node) => Frontmatter.FileLine(node.Start);

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")]
    private static partial Regex Date();

    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})T(?:[01][0-9]|2[0-3]):[0-5][0-9](?::[0-5][0-9](?:\.[0-9]+)?)?(?<offset>Z|[+-](?:[01][0-9]|2[0-3])(?::?[0-5][0-9])?)?$")]
    private static partial Regex IsoDateTime();

    /// <summary>§7: <c>&lt;producer&gt;/&lt;version&gt;</c>, <c>human:&lt;id&gt;</c> or <c>process:&lt;id&gt;</c>. A
    /// producer holds no <c>:</c>, so an undefined prefix such as <c>team:</c> is not taken for one.</summary>
    [GeneratedRegex(@"^(?:(?:human|process):\S+|[^\s/:]+/\S+)$")]
    private static partial Regex Actor();
}
