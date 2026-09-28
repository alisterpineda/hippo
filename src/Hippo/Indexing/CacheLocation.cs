using System.Security.Cryptography;
using System.Text;

namespace Hippo.Indexing;

/// <summary>
/// Where a notebook's index lives: <c>&lt;user cache&gt;/hippo/&lt;hash of notebook root&gt;/index.db</c>, or
/// <c>$HIPPO_CACHE_DIR/&lt;hash&gt;/index.db</c>. Never in the notebook: the config travels with it across machines,
/// and a synced folder breaks WAL.
/// </summary>
internal static class CacheLocation
{
    public const string CacheDirVariable = "HIPPO_CACHE_DIR";

    public static string DatabasePath(string notebookRoot, Func<string, string?> getEnvironmentVariable)
    {
        var cacheDir = NonEmpty(getEnvironmentVariable(CacheDirVariable));
        if (cacheDir is not null && !Path.IsPathRooted(cacheDir))
        {
            // A relative path would resolve against the working directory: a different index per folder, possibly
            // inside the notebook itself.
            throw new HippoException($"{CacheDirVariable} must be an absolute path; got '{cacheDir}'");
        }
        var cacheRoot = cacheDir ?? Path.Combine(UserCacheDirectory(getEnvironmentVariable), "hippo");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(notebookRoot)));
        return Path.Combine(cacheRoot, hash, "index.db");
    }

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
