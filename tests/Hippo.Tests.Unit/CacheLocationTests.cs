using Hippo.Cache;

namespace Hippo.Tests.Unit;

public class CacheLocationTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Hippo_cache_dir_replaces_the_user_cache_folder()
    {
        var path = CacheLocation.DatabasePath("/notes", Env(("HIPPO_CACHE_DIR", "/tmp/cache"), ("HOME", "/home/me")));

        var hash = Path.GetFileName(Path.GetDirectoryName(path))!;
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(Path.Combine("/tmp/cache", hash, "index.db"), path);
    }

    [Fact]
    public void Each_workspace_root_gets_its_own_folder()
    {
        var env = Env(("HIPPO_CACHE_DIR", "/tmp/cache"));

        Assert.Equal(CacheLocation.DatabasePath("/notes", env), CacheLocation.DatabasePath("/notes", env));
        Assert.NotEqual(CacheLocation.DatabasePath("/notes", env), CacheLocation.DatabasePath("/work", env));
    }

    [Fact]
    public void An_index_folder_is_named_by_the_sha256_of_its_root()
    {
        var env = Env(("HIPPO_CACHE_DIR", "/tmp/cache"));

        // printf '/notes' | shasum -a 256
        Assert.Equal("46bd1cc6315ac282a32b61f24a8937664346f79c43b44ad47d3a95b919a26600", CacheLocation.FolderName("/notes"));
        Assert.Equal(Path.Combine(CacheLocation.CacheRoot(env), CacheLocation.FolderName("/notes"), "index.db"),
            CacheLocation.DatabasePath("/notes", env));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.1.removing", false)]
    public void Only_64_lowercase_hex_digits_name_an_index_folder(string name, bool expected)
    {
        Assert.Equal(expected, CacheLocation.IsFolderName(name));
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
