using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.RepresentationModel;

namespace Hippo.Workspaces;

/// <summary>
/// A link out of a page. <see cref="Kind"/> is <c>body</c> or <c>frontmatter</c>; <see cref="Type"/> is <c>path</c>,
/// <c>url</c> or <c>anchor</c> (within the page). A path link's <see cref="Target"/> is the workspace key it resolves to,
/// or null when it leaves the workspace (<c>""</c> for the workspace root itself). <see cref="Line"/> counts from 1 at the
/// top of the file.
/// </summary>
internal sealed record Link(int Line, string Kind, string Type, string Raw, string? Target);

/// <summary>A parsed page. <see cref="BodyError"/> says why the body could not be parsed, in which case
/// <see cref="Links"/> holds only its frontmatter links.</summary>
internal sealed record ParsedPage(FrontmatterResult Frontmatter, List<Link> Links, string? BodyError = null);

/// <summary>Parses a markdown page: its frontmatter, and its links from the body (through Markdig), resolved from the
/// page's folder (or its bundle root when they start with <c>/</c>), and from the frontmatter fields the config names,
/// each resolved per the config.</summary>
internal static partial class Page
{
    // Plain CommonMark: [[x]] stays text.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    public static ParsedPage Parse(string path, ReadOnlySpan<byte> utf8, LinkSettings settings) =>
        Parse(path, Encoding.UTF8.GetString(utf8), settings);

    public static ParsedPage Parse(string path, string text, LinkSettings settings)
    {
        var block = Frontmatter.Read(text);
        var bundle = BundleRoot(path, settings.Bundles);
        var links = new List<Link>();

        if (block.Root is { } root)
        {
            foreach (var field in settings.Frontmatter)
            {
                foreach (var value in Values(root, field.Parts, 0))
                {
                    links.Add(Resolve(path, bundle, field.Resolve, Frontmatter.FileLine(value.Start), "frontmatter", value.Value!, isUrl: false));
                }
            }
        }

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(block.Body, Pipeline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Markdig throws on some inputs, such as blocks nested past its depth limit; one such page must not stop
            // the rest of the workspace from indexing.
            return new ParsedPage(block.Result, links.OrderBy(link => link.Line).ToList(), ex.Message);
        }

        foreach (var node in document.Descendants())
        {
            var line = node.Line + block.BodyLine;
            switch (node)
            {
                case LinkInline link:
                    links.Add(Resolve(path, bundle, LinkBase.Page, line, "body", link.Url ?? "", isUrl: true));
                    break;
                case AutolinkInline { IsEmail: true } email:
                    links.Add(new Link(line, "body", "url", email.Url, null));
                    break;
                case AutolinkInline autolink:
                    links.Add(Resolve(path, bundle, LinkBase.Page, line, "body", autolink.Url, isUrl: true));
                    break;
            }
        }

        return new ParsedPage(block.Result, links.OrderBy(link => link.Line).ToList());
    }

    /// <summary>The scalar values at <paramref name="parts"/> under <paramref name="node"/>. A value of any other shape
    /// than the field declares, or an empty one, is not a link.</summary>
    private static IEnumerable<YamlScalarNode> Values(YamlNode node, IReadOnlyList<(string Name, bool Each)> parts, int index)
    {
        if (index == parts.Count)
        {
            if (node is YamlScalarNode { Value.Length: > 0 } scalar && !Frontmatter.IsNull(scalar))
            {
                yield return scalar;
            }
            yield break;
        }

        if (node is not YamlMappingNode mapping)
        {
            yield break;
        }
        var (name, each) = parts[index];
        var child = mapping.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: var key } && key == name).Value;
        if (child is null)
        {
            yield break;
        }
        var items = each ? child is YamlSequenceNode sequence ? sequence.Children : [] : [child];
        foreach (var item in items)
        {
            foreach (var value in Values(item, parts, index + 1))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Classifies <paramref name="raw"/> and resolves it when it is a path. A leading <c>/</c> resolves against the
    /// page's bundle root; anything else against the page's folder or its bundle root, per <paramref name="resolve"/>.
    /// Body destinations are URLs, so a query is dropped and percent-encoding decoded; frontmatter values are literal
    /// paths. The fragment is never part of the target.
    /// </summary>
    private static Link Resolve(string path, string bundle, LinkBase resolve, int line, string kind, string raw, bool isUrl)
    {
        if (Scheme().IsMatch(raw) || raw.StartsWith("//", StringComparison.Ordinal))
        {
            return new Link(line, kind, "url", raw, null);
        }

        var target = raw;
        var end = isUrl ? target.IndexOfAny(['#', '?']) : target.IndexOf('#');
        if (end >= 0)
        {
            target = target[..end];
        }
        if (target.Length == 0)
        {
            return new Link(line, kind, "anchor", raw, null);
        }
        if (isUrl)
        {
            target = Uri.UnescapeDataString(target);
        }

        string from;
        if (target.StartsWith('/'))
        {
            from = bundle;
            target = target.TrimStart('/');
        }
        else
        {
            from = resolve == LinkBase.Page ? Folder(path) : bundle;
        }
        return new Link(line, kind, "path", raw, Normalize(from, target));
    }

    /// <summary>Joins <paramref name="relative"/> onto <paramref name="folder"/> and removes <c>.</c> and <c>..</c>
    /// segments. Null when the result climbs above the workspace root; <c>""</c> when it is the root itself.</summary>
    private static string? Normalize(string folder, string relative)
    {
        var parts = new List<string>();
        foreach (var part in $"{folder}/{relative}".Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }
                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }
        return string.Join('/', parts);
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>The root of the deepest bundle holding <paramref name="path"/>, or the workspace root (<c>""</c>) when
    /// no bundle holds it.</summary>
    private static string BundleRoot(string path, IReadOnlyList<string> bundles) =>
        bundles.Where(root => path.StartsWith(root + "/", StringComparison.Ordinal)).MaxBy(root => root.Length) ?? "";

    /// <summary>A URI scheme, at least two characters so a Windows drive letter is not one.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:")]
    private static partial Regex Scheme();
}
