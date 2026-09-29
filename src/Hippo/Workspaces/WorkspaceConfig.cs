using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Hippo.Workspaces;

/// <summary>What a relative frontmatter link resolves against: the folder of the page it is on, or the root of that
/// page's bundle.</summary>
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

/// <summary>
/// The <c>links</c> section: every setting that shapes the links stored for a page, and nothing else. It is all that
/// <see cref="Page.Parse(string, string, LinkSettings)"/> reads, so a setting it comes to need is added here, beside the
/// <see cref="Fingerprint"/> that must cover it. <see cref="Bundles"/> are folder roots, relative to the workspace root
/// without leading or trailing <c>/</c>.
/// </summary>
internal sealed record LinkSettings(IReadOnlyList<string> Bundles, IReadOnlyList<FrontmatterLinkField> Frontmatter)
{
    public static LinkSettings Default { get; } = new([], []);

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
/// <c>.hippo/config.json</c>. A key it does not know is an error, so a misspelt one is caught rather than silently
/// left at its default. Comments and trailing commas are allowed. <see cref="Gitignore"/> leaves out the files git
/// ignores, whatever <see cref="Include"/> says.
/// </summary>
internal sealed record WorkspaceConfig(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    /// <summary>The folder at the workspace root that holds <see cref="FileName"/>.</summary>
    public const string Folder = ".hippo";

    public const string FileName = "config.json";

    /// <summary>The config's path relative to the workspace root, as messages show it.</summary>
    public const string RelativePath = Folder + "/" + FileName;

    /// <summary>What <c>hippo init</c> writes: every file but the ones git ignores and the usual tool folders, with the
    /// <c>links</c> section shown commented out. It sets only what differs from <see cref="Default"/>; the annotated example
    /// in README.md spells out every key, so change both together. A unit test parses the commented section to catch stale
    /// syntax.</summary>
    public const string Starter = """
        {
          "files": {
            "exclude": [".git/**", ".obsidian/**", ".trash/**"]
          },
          // "links": {
          //   "bundles": ["wiki"],                // a leading "/" in a link on a page in wiki resolves against wiki
          //   "frontmatter": [
          //     {
          //       "field": "sources[].resource",  // dotted for nested mappings; [] for each element of a list
          //       "resolve": "bundle"             // page (the page's own folder, the default) or bundle (its bundle root)
          //     }
          //   ]
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

    public static WorkspaceConfig Default { get; } = new(["**/*"], []);

    public bool Gitignore { get; init; } = true;

    public LinkSettings Links { get; init; } = LinkSettings.Default;

    /// <summary>The config's full path in the workspace at <paramref name="root"/>.</summary>
    public static string PathIn(string root) => Path.Combine(root, Folder, FileName);

    public static WorkspaceConfig Parse(string json)
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
            foreach (var (key, value) in Properties(null, document.RootElement, "files", "links"))
            {
                config = key switch
                {
                    "files" => ParseFiles(config, value),
                    "links" => config with { Links = ParseLinks(value) },
                    _ => throw new UnreachableException(key),
                };
            }
            return config;
        }
    }

    private static WorkspaceConfig ParseFiles(WorkspaceConfig config, JsonElement element)
    {
        foreach (var (key, value) in Object("files", element, "include", "exclude", "gitignore"))
        {
            config = key switch
            {
                "include" => config with { Include = Patterns("files.include", value) },
                "exclude" => config with { Exclude = Patterns("files.exclude", value) },
                "gitignore" => config with { Gitignore = Boolean("files.gitignore", value) },
                _ => throw new UnreachableException(key),
            };
        }
        return config;
    }

    private static LinkSettings ParseLinks(JsonElement element)
    {
        var links = LinkSettings.Default;
        foreach (var (key, value) in Object("links", element, "bundles", "frontmatter"))
        {
            links = key switch
            {
                "bundles" => links with { Bundles = ParseBundles(value) },
                "frontmatter" => links with { Frontmatter = ParseFrontmatterLinks(value) },
                _ => throw new UnreachableException(key),
            };
        }
        return links;
    }

    private static List<string> ParseBundles(JsonElement element)
    {
        const string usage = "links.bundles must be an array of folders inside the workspace";
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error(usage);
        }

        var roots = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var root = String(item)?.Trim('/') ?? throw Error(usage);
            if (root.Length == 0 || root.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw Error(usage);
            }
            roots.Add(root);
        }
        return roots;
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
            foreach (var (key, value) in Object("links.frontmatter[]", item, "field", "resolve"))
            {
                switch (key)
                {
                    case "field":
                        field = String(value) ?? throw Error(usage);
                        break;
                    case "resolve":
                        resolve = Resolve("links.frontmatter[].resolve", value);
                        break;
                    default:
                        throw new UnreachableException(key);
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

    private static IEnumerable<(string Key, JsonElement Value)> Object(string key, JsonElement element, params string[] known)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error($"{key} must be an object");
        }
        return Properties(key, element, known);
    }

    /// <summary>The properties of the object <paramref name="element"/> at <paramref name="key"/> (null for the top
    /// level), each of which must be one of <paramref name="known"/>.</summary>
    private static List<(string Key, JsonElement Value)> Properties(string? key, JsonElement element, params string[] known)
    {
        var properties = element.EnumerateObject().Select(property => (property.Name, property.Value)).ToList();
        foreach (var (name, _) in properties)
        {
            if (!known.Contains(name))
            {
                var path = key is null ? name : $"{key}.{name}";
                throw Error($"unknown key {path}; expected {string.Join(" or ", known)}");
            }
        }
        return properties;
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

    private static bool Boolean(string key, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Error($"{key} must be true or false"),
    };

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
