using System.Buffers;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hippo.Notebooks;

/// <summary>What a relative link resolves against: the folder of the page it is on, or the root of that page's bundle.</summary>
internal enum LinkBase
{
    Page,
    Bundle,
}

/// <summary>A frontmatter field whose values are links. <see cref="Field"/> is dotted for nested mappings, and a part
/// ending in <c>[]</c> stands for each element of a list, as in <c>sources[].resource</c>.</summary>
internal sealed record FrontmatterLinkField(string Field, LinkBase Resolve)
{
    public IReadOnlyList<(string Name, bool Each)> Parts { get; } = Field.Split('.')
        .Select(part => part.EndsWith("[]", StringComparison.Ordinal) ? (part[..^2], true) : (part, false))
        .ToList();

    /// <summary>Whether <see cref="Field"/> is dotted names, each optionally ending in <c>[]</c>, with no other
    /// brackets.</summary>
    public bool IsValid => Parts.All(part => part.Name.Length > 0 && part.Name.IndexOfAny(['[', ']']) < 0);
}

/// <summary>The <c>links</c> section: how body links resolve, which frontmatter fields hold links, and the
/// <see cref="Roots"/> (globs) that need no inbound link to not be orphans.</summary>
internal sealed record LinkConfig(LinkBase Body, IReadOnlyList<FrontmatterLinkField> Frontmatter, IReadOnlyList<string> Roots)
{
    public static LinkConfig Default { get; } = new(LinkBase.Page, [], []);
}

/// <summary>
/// Every setting that shapes the links stored for a page, and nothing else: all that <see cref="Page.Parse(string, string, LinkSettings)"/>
/// reads, so a setting it comes to need is added here, beside the <see cref="Fingerprint"/> that must cover it.
/// </summary>
internal sealed record LinkSettings(IReadOnlyList<string> Bundles, LinkBase Body, IReadOnlyList<FrontmatterLinkField> Frontmatter)
{
    /// <summary>These settings as one string. The index keeps the value its links were extracted under, and
    /// re-extracts them all when it differs.</summary>
    public string Fingerprint
    {
        get
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("bundles");
                foreach (var bundle in Bundles)
                {
                    writer.WriteStringValue(bundle);
                }
                writer.WriteEndArray();
                writer.WriteString("body", Name(Body));
                writer.WriteStartArray("frontmatter");
                foreach (var link in Frontmatter)
                {
                    writer.WriteStartObject();
                    writer.WriteString("field", link.Field);
                    writer.WriteString("resolve", Name(link.Resolve));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    private static string Name(LinkBase resolve) => resolve == LinkBase.Page ? "page" : "bundle";
}

/// <summary>
/// The parts of <c>.hippo.yaml</c> this hippo understands. Keys it does not know, such as those a later phase adds,
/// are ignored so an older hippo still runs against a newer notebook.
/// </summary>
internal sealed record NotebookConfig(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    public const string FileName = ".hippo.yaml";

    /// <summary>What <c>hippo init</c> writes: every file but the usual tool folders, with the other sections shown
    /// commented out. It mirrors the annotated example in README.md (less the default <c>wikilinks: text</c>), so change
    /// both together; a unit test parses the commented sections to catch stale syntax.</summary>
    public const string Starter = """
        files:
          include: ["**/*"]
          exclude: [".git/**", ".obsidian/**", ".trash/**"]
        # bundles:
        #   - root: wiki                   # a leading "/" in a link resolves against this folder
        # links:
        #   body: { resolve: page }        # page (the page's own folder) or bundle (its bundle root)
        #   frontmatter:
        #     - field: sources[].resource  # dotted for nested mappings; [] for each element of a list
        #       resolve: bundle
        #   roots: ["wiki/index.md"]       # pages that are not orphans without inbound links

        """;

    private const int MaxDepth = 64;

    public static NotebookConfig Default { get; } = new(["**/*"], []);

    /// <summary>Bundle roots, relative to the notebook root without leading or trailing <c>/</c>.</summary>
    public IReadOnlyList<string> Bundles { get; init; } = [];

    public LinkConfig Links { get; init; } = LinkConfig.Default;

    /// <summary>The settings that shape the links stored for a page.</summary>
    public LinkSettings LinkSettings => new(Bundles, Links.Body, Links.Frontmatter);

    public static NotebookConfig Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new DepthLimitedParser(new Parser(new StringReader(yaml)), MaxDepth));
        }
        catch (YamlException ex)
        {
            throw Error($"line {ex.Start.Line}: invalid YAML");
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
        {
            return Default;
        }
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw Error("the top level must be a mapping");
        }

        var config = Default;
        foreach (var (keyNode, value) in root.Children)
        {
            switch (Key(keyNode))
            {
                case "files":
                    config = ParseFiles(config, value);
                    break;
                case "bundles":
                    config = config with { Bundles = ParseBundles(value) };
                    break;
                case "links":
                    config = config with { Links = ParseLinks(value) };
                    break;
            }
        }
        return config;
    }

    private static NotebookConfig ParseFiles(NotebookConfig config, YamlNode node)
    {
        foreach (var (key, value) in Mapping("files", node))
        {
            switch (key)
            {
                case "include":
                    config = config with { Include = Patterns("files.include", value) };
                    break;
                case "exclude":
                    config = config with { Exclude = Patterns("files.exclude", value) };
                    break;
            }
        }
        return config;
    }

    private static List<string> ParseBundles(YamlNode node)
    {
        const string usage = "bundles must be a list of mappings, each with a root folder inside the notebook";
        if (node is not YamlSequenceNode sequence)
        {
            throw Error(usage);
        }

        var roots = new List<string>();
        foreach (var item in sequence.Children)
        {
            string? root = null;
            foreach (var (key, value) in Mapping("bundles", item))
            {
                if (key == "root")
                {
                    root = value is YamlScalarNode { Value: { } text } ? text.Trim('/') : throw Error(usage);
                }
            }
            if (root is null || root.Length == 0 || root.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw Error(usage);
            }
            roots.Add(root);
        }
        return roots;
    }

    private static LinkConfig ParseLinks(YamlNode node)
    {
        var links = LinkConfig.Default;
        foreach (var (key, value) in Mapping("links", node))
        {
            switch (key)
            {
                case "body":
                    foreach (var (bodyKey, bodyValue) in Mapping("links.body", value))
                    {
                        if (bodyKey == "resolve")
                        {
                            links = links with { Body = Resolve("links.body.resolve", bodyValue) };
                        }
                    }
                    break;
                case "wikilinks":
                    // Resolving [[x]] is an open question; until it is settled, a notebook asking for it is told so
                    // rather than getting answers that silently ignore its wikilinks.
                    if (value is not YamlScalarNode { Value: "text" })
                    {
                        throw Error("links.wikilinks must be text; this version of hippo does not resolve wikilinks");
                    }
                    break;
                case "frontmatter":
                    links = links with { Frontmatter = ParseFrontmatterLinks(value) };
                    break;
                case "roots":
                    links = links with { Roots = Patterns("links.roots", value) };
                    break;
            }
        }
        return links;
    }

    private static List<FrontmatterLinkField> ParseFrontmatterLinks(YamlNode node)
    {
        const string usage = "links.frontmatter must be a list of mappings, each with a field such as sources[].resource";
        if (node is not YamlSequenceNode sequence)
        {
            throw Error(usage);
        }

        var fields = new List<FrontmatterLinkField>();
        foreach (var item in sequence.Children)
        {
            string? field = null;
            var resolve = LinkBase.Page;
            foreach (var (key, value) in Mapping("links.frontmatter", item))
            {
                switch (key)
                {
                    case "field":
                        field = value is YamlScalarNode { Value: { } text } ? text : throw Error(usage);
                        break;
                    case "resolve":
                        resolve = Resolve("links.frontmatter[].resolve", value);
                        break;
                }
            }
            var link = field is null ? null : new FrontmatterLinkField(field, resolve);
            if (link is null || !link.IsValid)
            {
                throw Error(usage);
            }
            fields.Add(link);
        }
        return fields;
    }

    private static LinkBase Resolve(string key, YamlNode node) => node switch
    {
        YamlScalarNode { Value: "page" } => LinkBase.Page,
        YamlScalarNode { Value: "bundle" } => LinkBase.Bundle,
        _ => throw Error($"{key} must be page or bundle"),
    };

    private static IEnumerable<(string Key, YamlNode Value)> Mapping(string key, YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw Error($"{key} must be a mapping");
        }
        return mapping.Children.Select(child => (Key(child.Key), child.Value));
    }

    private static List<string> Patterns(string key, YamlNode node)
    {
        if (node is not YamlSequenceNode sequence)
        {
            throw Error($"{key} must be a list of glob patterns");
        }
        return sequence.Children
            .Select(item => item is YamlScalarNode { Value: { Length: > 0 } pattern }
                ? pattern
                : throw Error($"{key} must be a list of glob patterns"))
            .ToList();
    }

    private static string Key(YamlNode node) =>
        node is YamlScalarNode { Value: { } key } ? key : throw Error($"line {node.Start.Line}: keys must be plain values");

    private static HippoException Error(string message) => new($"{FileName}: {message}");
}
