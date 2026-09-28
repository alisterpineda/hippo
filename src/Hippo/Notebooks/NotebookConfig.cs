using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Hippo.Notebooks;

/// <summary>
/// The parts of <c>.hippo.yaml</c> this hippo understands. Keys it does not know, such as those a later phase adds,
/// are ignored with a warning so an older hippo still runs against a newer notebook.
/// </summary>
internal sealed record NotebookConfig(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    public const string FileName = ".hippo.yaml";

    private const int MaxDepth = 64;

    public static NotebookConfig Default { get; } = new(["**/*"], []);

    public static NotebookConfig Parse(string yaml, ICollection<string> warnings)
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
                case "version":
                    if (value is not YamlScalarNode { Value: "1" })
                    {
                        throw Error("version must be 1");
                    }
                    break;
                case "files":
                    config = ParseFiles(value, warnings);
                    break;
                case var key:
                    warnings.Add(UnknownKey(key));
                    break;
            }
        }
        return config;
    }

    private static NotebookConfig ParseFiles(YamlNode node, ICollection<string> warnings)
    {
        if (node is not YamlMappingNode files)
        {
            throw Error("files must be a mapping");
        }

        var config = Default;
        foreach (var (keyNode, value) in files.Children)
        {
            switch (Key(keyNode))
            {
                case "include":
                    config = config with { Include = Patterns("files.include", value) };
                    break;
                case "exclude":
                    config = config with { Exclude = Patterns("files.exclude", value) };
                    break;
                case var key:
                    warnings.Add(UnknownKey($"files.{key}"));
                    break;
            }
        }
        return config;
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

    private static string UnknownKey(string key) =>
        $"{FileName}: '{key}' is not understood by this version of hippo and was ignored";

    private static HippoException Error(string message) => new($"{FileName}: {message}");
}
