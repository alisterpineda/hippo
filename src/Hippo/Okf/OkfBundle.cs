using System.Text;
using Hippo.Workspaces;
using YamlDotNet.RepresentationModel;

namespace Hippo.Okf;

/// <summary>
/// A bundle from <c>bundles</c> whose root <c>index.md</c> declares <c>okf_version</c> in its frontmatter (§12).
/// <see cref="Version"/> is the declared value as written, or null when it is not a plain value. hippo reads every OKF
/// bundle as <see cref="SpecVersion"/>, whatever it declares.
/// </summary>
internal sealed record OkfBundle(string Root, string? Version)
{
    public const string SpecVersion = "0.2";

    public const string VersionKey = "okf_version";

    /// <summary>The names OKF reserves for a folder's index (§8) and a bundle's log.</summary>
    public const string IndexName = "index.md";

    public const string LogName = "log.md";

    /// <summary>The key of the <c>index.md</c> at <paramref name="root"/>.</summary>
    public static string IndexOf(string root) => root + "/" + IndexName;

    /// <summary>Whether the file at <paramref name="path"/> is an <c>index.md</c> or a <c>log.md</c>.</summary>
    public static bool IsReserved(string path) => path[(path.LastIndexOf('/') + 1)..] is IndexName or LogName;

    /// <summary>The OKF bundles among the workspace's bundles, read from the root <c>index.md</c> of each when it is
    /// among <paramref name="files"/>. A bundle whose <c>index.md</c> cannot be read is a plain bundle until it can; the
    /// sweep warns about the file when it reads it in turn.</summary>
    public static List<OkfBundle> Find(Workspace workspace, IReadOnlySet<string> files)
    {
        var bundles = new List<OkfBundle>();
        foreach (var root in workspace.Config.Bundles.Distinct(StringComparer.Ordinal))
        {
            var index = IndexOf(root);
            if (!files.Contains(index))
            {
                continue;
            }
            string text;
            try
            {
                text = Encoding.UTF8.GetString(File.ReadAllBytes(workspace.FullPath(index)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (Frontmatter.Read(text).Root is { } frontmatter && Frontmatter.Field(frontmatter, VersionKey) is { } declared
                && !Frontmatter.IsNull(declared.Value))
            {
                bundles.Add(new OkfBundle(root, declared.Value is YamlScalarNode version ? version.Value : null));
            }
        }
        return bundles;
    }
}
