using System.Security.Cryptography;
using System.Text;

namespace Hippo.Indexing;

/// <summary>
/// Where a workspace's index lives: <c>&lt;user cache&gt;/hippo/&lt;hash of workspace root&gt;/index.db</c>, or
/// <c>$HIPPO_CACHE_DIR/&lt;hash&gt;/index.db</c>. Never in the workspace: the config travels with it across machines,
/// and a synced folder breaks WAL. Keyed by the root's path, not by an id kept in the config, because copies of a
/// workspace (clones, worktrees, a copied folder) carry the same config: each live folder has its own path, so no two
/// workspaces ever share an index.
/// </summary>
internal static class CacheLocation
{
    public const string CacheDirVariable = "HIPPO_CACHE_DIR";

    public const string DatabaseName = "index.db";

    /// <summary>The index of the workspace at <paramref name="canonicalRoot"/>, a root from
    /// <see cref="CanonicalPath.Of"/>.</summary>
    public static string DatabasePath(string canonicalRoot, Func<string, string?> getEnvironmentVariable) =>
        Path.Combine(CacheRoot(getEnvironmentVariable), FolderName(canonicalRoot), DatabaseName);

    /// <summary>The folder that holds a folder per index.</summary>
    public static string CacheRoot(Func<string, string?> getEnvironmentVariable)
    {
        var cacheDir = NonEmpty(getEnvironmentVariable(CacheDirVariable));
        if (cacheDir is not null && !Path.IsPathRooted(cacheDir))
        {
            // A relative path would resolve against the working directory: a different index per folder, possibly
            // inside the workspace itself.
            throw new HippoException($"{CacheDirVariable} must be an absolute path; got '{cacheDir}'");
        }
        return cacheDir ?? Path.Combine(UserCacheDirectory(getEnvironmentVariable), "hippo");
    }

    /// <summary>The name of the folder that holds the index of <paramref name="canonicalRoot"/>: 64 lowercase hex
    /// digits.</summary>
    public static string FolderName(string canonicalRoot) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot)));

    /// <summary>Whether <paramref name="name"/> could be a <see cref="FolderName"/>.</summary>
    public static bool IsFolderName(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigitLower);

    private static string UserCacheDirectory(Func<string, string?> getEnvironmentVariable)
    {
        if (OperatingSystem.IsWindows())
        {
            return NonEmpty(getEnvironmentVariable("LOCALAPPDATA"))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var home = NonEmpty(getEnvironmentVariable("HOME")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches");
        }
        return NonEmpty(getEnvironmentVariable("XDG_CACHE_HOME")) is { } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(home, ".cache");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
