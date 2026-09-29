using Hippo.Indexing;

namespace Hippo.Tests.Unit;

public class CacheLocationTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Hippo_cache_dir_replaces_the_user_cache_folder()
    {
        var path = CacheLocation.DatabasePath("/notes", Env(("HIPPO_CACHE_DIR", "/tmp/cache"), ("HOME", "/home/me")));

        Assert.Equal("index.db", Path.GetFileName(path));
        Assert.Equal("/tmp/cache", Path.GetDirectoryName(Path.GetDirectoryName(path)));
        Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Each_workspace_root_gets_its_own_folder()
    {
        var env = Env(("HIPPO_CACHE_DIR", "/tmp/cache"));

        Assert.Equal(CacheLocation.DatabasePath("/notes", env), CacheLocation.DatabasePath("/notes", env));
        Assert.NotEqual(CacheLocation.DatabasePath("/notes", env), CacheLocation.DatabasePath("/work", env));
    }

    [Fact]
    public void The_default_is_the_user_cache_folder_for_this_os()
    {
        var env = Env(("HOME", "/home/me"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"));

        var cacheRoot = Path.GetDirectoryName(Path.GetDirectoryName(CacheLocation.DatabasePath("/notes", env)));

        var expected = OperatingSystem.IsMacOS() ? "/home/me/Library/Caches/hippo"
            : OperatingSystem.IsWindows() ? @"C:\Users\me\AppData\Local\hippo"
            : "/home/me/.cache/hippo";
        Assert.Equal(expected, cacheRoot);
    }

    [Fact]
    public void Xdg_cache_home_is_the_user_cache_folder_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG_CACHE_HOME applies on Linux only");

        var path = CacheLocation.DatabasePath("/notes", Env(("HOME", "/home/me"), ("XDG_CACHE_HOME", "/xdg")));

        Assert.StartsWith("/xdg/hippo/", path);
    }

    [Fact]
    public void An_empty_hippo_cache_dir_counts_as_unset()
    {
        var env = Env(("HOME", "/home/me"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"));

        Assert.Equal(CacheLocation.DatabasePath("/notes", env),
            CacheLocation.DatabasePath("/notes", Env(("HIPPO_CACHE_DIR", ""), ("HOME", "/home/me"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"))));
    }

    [Fact]
    public void A_relative_hippo_cache_dir_is_an_error()
    {
        var ex = Assert.Throws<HippoException>(() => CacheLocation.DatabasePath("/notes", Env(("HIPPO_CACHE_DIR", "~/cache"))));

        Assert.Contains("HIPPO_CACHE_DIR", ex.Message);
    }

    [Fact]
    public void A_relative_xdg_cache_home_is_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG_CACHE_HOME applies on Linux only");

        var path = CacheLocation.DatabasePath("/notes", Env(("HOME", "/home/me"), ("XDG_CACHE_HOME", "relative")));

        Assert.StartsWith("/home/me/.cache/hippo/", path);
    }
}
