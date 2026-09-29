using System.Buffers;
using System.Text;
using System.Text.Json;

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
/// The parts of <c>.hippo/config.json</c> this hippo understands. Keys it does not know, such as those a later phase
/// adds, are ignored so an older hippo still runs against a newer notebook. Comments and trailing commas are allowed.
/// </summary>
internal sealed record NotebookConfig(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    /// <summary>The folder at the notebook root that holds <see cref="FileName"/>.</summary>
    public const string Folder = ".hippo";

    public const string FileName = "config.json";

    /// <summary>The config's path relative to the notebook root, as messages show it.</summary>
    public const string RelativePath = Folder + "/" + FileName;

    /// <summary>What <c>hippo init</c> writes: every file but the usual tool folders, with the other sections shown
    /// commented out. It mirrors the annotated example in README.md (less the default <c>"wikilinks": "text"</c>), so
    /// change both together; a unit test parses the commented sections to catch stale syntax.</summary>
    public const string Starter = """
        {
          "files": {
            "include": ["**/*"],
            "exclude": [".git/**", ".obsidian/**", ".trash/**"]
          },
          // "bundles": [
          //   { "root": "wiki" }                  // a leading "/" in a link resolves against this folder
          // ],
          // "links": {
          //   "body": { "resolve": "page" },      // page (the page's own folder) or bundle (its bundle root)
          //   "frontmatter": [
          //     {
          //       "field": "sources[].resource",  // dotted for nested mappings; [] for each element of a list
          //       "resolve": "bundle"
          //     }
          //   ],
          //   "roots": ["wiki/index.md"]          // pages that are not orphans without inbound links
          // }
        }

        """;

    private const int MaxDepth = 64;

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowDuplicateProperties = false,
        MaxDepth = MaxDepth,
    };

    public static NotebookConfig Default { get; } = new(["**/*"], []);

    /// <summary>Bundle roots, relative to the notebook root without leading or trailing <c>/</c>.</summary>
    public IReadOnlyList<string> Bundles { get; init; } = [];

    public LinkConfig Links { get; init; } = LinkConfig.Default;

    /// <summary>The settings that shape the links stored for a page.</summary>
    public LinkSettings LinkSettings => new(Bundles, Links.Body, Links.Frontmatter);

    /// <summary>The config's full path in the notebook at <paramref name="root"/>.</summary>
    public static string PathIn(string root) => Path.Combine(root, Folder, FileName);

    public static NotebookConfig Parse(string json)
    {
        if (HasNoTokens(json))
        {
            return Default;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            // A duplicate key is reported without a position; its message names the key instead.
            throw Error(ex.LineNumber is { } line ? $"line {line + 1}: invalid JSON" : $"invalid JSON: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // An escaped lone surrogate in a key, found when keys are unescaped to check for duplicates.
            throw Error($"invalid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Error("the top level must be an object");
            }

            var config = Default;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "files":
                        config = ParseFiles(config, property.Value);
                        break;
                    case "bundles":
                        config = config with { Bundles = ParseBundles(property.Value) };
                        break;
                    case "links":
                        config = config with { Links = ParseLinks(property.Value) };
                        break;
                }
            }
            return config;
        }
    }

    private static NotebookConfig ParseFiles(NotebookConfig config, JsonElement element)
    {
        foreach (var (key, value) in Object("files", element))
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

    private static List<string> ParseBundles(JsonElement element)
    {
        const string usage = "bundles must be an array of objects, each with a root folder inside the notebook";
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error(usage);
        }

        var roots = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            string? root = null;
            foreach (var (key, value) in Object("bundles", item))
            {
                if (key == "root")
                {
                    root = String(value)?.Trim('/') ?? throw Error(usage);
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

    private static LinkConfig ParseLinks(JsonElement element)
    {
        var links = LinkConfig.Default;
        foreach (var (key, value) in Object("links", element))
        {
            switch (key)
            {
                case "body":
                    foreach (var (bodyKey, bodyValue) in Object("links.body", value))
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
                    if (String(value) != "text")
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

    private static List<FrontmatterLinkField> ParseFrontmatterLinks(JsonElement element)
    {
        const string usage = "links.frontmatter must be an array of objects, each with a field such as sources[].resource";
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error(usage);
        }

        var fields = new List<FrontmatterLinkField>();
        foreach (var item in element.EnumerateArray())
        {
            string? field = null;
            var resolve = LinkBase.Page;
            foreach (var (key, value) in Object("links.frontmatter", item))
            {
                switch (key)
                {
                    case "field":
                        field = String(value) ?? throw Error(usage);
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

    private static LinkBase Resolve(string key, JsonElement element) => String(element) switch
    {
        "page" => LinkBase.Page,
        "bundle" => LinkBase.Bundle,
        _ => throw Error($"{key} must be page or bundle"),
    };

    private static IEnumerable<(string Key, JsonElement Value)> Object(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error($"{key} must be an object");
        }
        return element.EnumerateObject().Select(property => (property.Name, property.Value));
    }

    private static List<string> Patterns(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error($"{key} must be an array of glob patterns");
        }
        return element.EnumerateArray()
            .Select(item => String(item) is { Length: > 0 } pattern
                ? pattern
                : throw Error($"{key} must be an array of glob patterns"))
            .ToList();
    }

    /// <summary>The element's text when it is a JSON string, else null.</summary>
    private static string? String(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException ex)
        {
            // An escaped lone surrogate, which no .NET string can be decoded from.
            throw Error($"invalid JSON: {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="json"/> holds only whitespace and comments, which reads as the default
    /// config as an empty file does.</summary>
    private static bool HasNoTokens(string json)
    {
        // Not the final block, so running out of input is not itself an error; the appended newline ends a trailing
        // line comment so it counts as consumed, while an unclosed block comment still does not.
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false,
            new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip }));
        try
        {
            return !reader.Read() && reader.BytesConsumed == bytes.Length;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HippoException Error(string message) => new($"{RelativePath}: {message}");
}
