using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hippo.Okf;

namespace Hippo.Workspaces;

/// <summary>What a relative frontmatter link resolves against: the folder of the page it is on, or the root of that
/// page's bundle.</summary>
internal enum LinkBase
{
    Page,
    Bundle,
}

/// <summary>A frontmatter field whose values are links, as a <see cref="FieldPath"/> such as
/// <c>sources[].resource</c>.</summary>
internal sealed record FrontmatterLinkField(string Field, LinkBase Resolve)
{
    public FieldPath Path { get; } = new(Field);
}

/// <summary>
/// The <c>links</c> section: which frontmatter fields hold links. Pages are parsed under a <see cref="PageSettings"/>,
/// which adds the workspace's bundles and what the sweep reads from the workspace itself.
/// </summary>
internal sealed record LinkSettings(IReadOnlyList<FrontmatterLinkField> Frontmatter)
{
    public static LinkSettings Default { get; } = new([]);
}

/// <summary>How the search table splits text into terms: <see cref="Porter"/>, FTS5's <c>porter unicode61</c>, matches
/// whole words and their other forms; <see cref="Trigram"/> matches any run of three or more characters, inside words
/// too.</summary>
internal enum SearchTokenizer
{
    Porter,
    Trigram,
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

    /// <summary>What <c>hippo init</c> writes: every file but the ones git ignores and the usual tool folders, with
    /// <c>bundles</c> and the <c>links</c> and <c>lint</c> sections shown commented out. It sets only what differs from
    /// <see cref="Default"/>; the annotated example in README.md spells out every key, so change both together. A unit
    /// test parses the commented sections to catch stale syntax.</summary>
    public const string Starter = """
        {
          "files": {
            "exclude": [".git/**", ".obsidian/**", ".trash/**"]
          },
          // "bundles": ["wiki"],                  // a leading "/" in a link on a page in wiki resolves against wiki
          // "links": {
          //   "frontmatter": [                    // an OKF bundle's path fields, such as sources[].resource, need no entry
          //     {
          //       "field": "related[]",           // dotted for nested mappings; [] for each element of a list
          //       "resolve": "page"               // page (the page's own folder, the default) or bundle (its bundle root)
          //     }
          //   ]
          // },
          // "lint": {
          //   "off": ["okf-footnote"],            // rules to leave unchecked; OKF MUST rules are always checked
          //   "exclude": ["archive/**"]           // files whose findings lint leaves out; they stay indexed and linkable
          // },
          // "search": {
          //   "tokenizer": "trigram"              // porter (whole words and their forms, the default) or trigram (any substring)
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

    /// <summary>The bundles' folder roots, relative to the workspace root without leading or trailing <c>/</c>. They
    /// decide how links resolve, which bundles are OKF bundles, and what <c>okf-index</c> covers.</summary>
    public IReadOnlyList<string> Bundles { get; init; } = [];

    public LinkSettings Links { get; init; } = LinkSettings.Default;

    /// <summary>The rules <c>lint.off</c> turns off.</summary>
    public IReadOnlyList<string> LintOff { get; init; } = [];

    /// <summary>The globs <c>lint.exclude</c> lists: <c>lint</c> leaves out the findings on files they match, and the
    /// sweep does not warn about their frontmatter or links failing to parse. The files stay indexed.</summary>
    public IReadOnlyList<string> LintExclude { get; init; } = [];

    public SearchTokenizer SearchTokenizer { get; init; } = SearchTokenizer.Porter;

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
            foreach (var (key, value) in Properties(null, document.RootElement, "files", "bundles", "links", "lint", "search"))
            {
                config = key switch
                {
                    "files" => ParseFiles(config, value),
                    "bundles" => config with { Bundles = ParseBundles(value) },
                    "links" => config with { Links = ParseLinks(value) },
                    "lint" => ParseLint(config, value),
                    "search" => config with { SearchTokenizer = ParseSearch(value) },
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
        foreach (var (_, value) in Object("links", element, "frontmatter"))
        {
            links = links with { Frontmatter = ParseFrontmatterLinks(value) };
        }
        return links;
    }

    private static List<string> ParseBundles(JsonElement element)
    {
        const string usage = "bundles must be an array of folders inside the workspace";
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
            if (link is null || !link.Path.IsValid)
            {
                throw Error(usage);
            }
            fields.Add(link);
        }
        return fields;
    }

    private static WorkspaceConfig ParseLint(WorkspaceConfig config, JsonElement element)
    {
        foreach (var (key, value) in Object("lint", element, "off", "exclude"))
        {
            config = key switch
            {
                "off" => config with { LintOff = ParseLintOff(value) },
                "exclude" => config with { LintExclude = Patterns("lint.exclude", value) },
                _ => throw new UnreachableException(key),
            };
        }
        return config;
    }

    /// <summary>The rules <c>lint.off</c> names. An OKF MUST rule cannot be turned off: with every MUST rule on, a run
    /// that reports none of them means the bundle is conformant (§11).</summary>
    private static List<string> ParseLintOff(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error("lint.off must be an array of rule names");
        }
        var off = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var name = String(item) ?? throw Error("lint.off must be an array of rule names");
            var rule = LintRules.Find(name)
                ?? throw Error($"lint.off: unknown rule {name}; expected one of {string.Join(", ", LintRules.Names)}");
            if (!rule.CanTurnOff)
            {
                throw Error($"lint.off: {name} cannot be turned off; it is a MUST rule in OKF v{OkfBundle.SpecVersion}");
            }
            off.Add(name);
        }
        return off;
    }

    private static SearchTokenizer ParseSearch(JsonElement element)
    {
        var tokenizer = SearchTokenizer.Porter;
        foreach (var (_, value) in Object("search", element, "tokenizer"))
        {
            tokenizer = String(value) switch
            {
                "porter" => SearchTokenizer.Porter,
                "trigram" => SearchTokenizer.Trigram,
                _ => throw Error("search.tokenizer must be porter or trigram"),
            };
        }
        return tokenizer;
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
