using System.Diagnostics;
using System.Runtime.InteropServices;
using Hippo.Cache;

namespace Hippo.Tests.Unit;

public class CanonicalPathTests
{
    [Fact]
    public void A_path_that_does_not_exist_is_kept_as_given()
    {
        using var dir = new TempDirectory();
        var missing = dir.Combine("missing");

        Assert.Equal(missing, CanonicalPath.Of(missing));
    }

    [Fact]
    public void Unix_keeps_a_path_as_given_even_through_a_symlink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only: getcwd already returns the physical path");
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("real"));
        var link = dir.Combine("link");
        Directory.CreateSymbolicLink(link, dir.Combine("real"));

        Assert.Equal(link, CanonicalPath.Of(link));
    }

    [Fact]
    public void Windows_resolves_the_stored_letter_case()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var dir = new TempDirectory();
        var stored = dir.Combine("Notes");
        Directory.CreateDirectory(stored);

        var canonical = CanonicalPath.Of(stored);

        Assert.Equal(canonical, CanonicalPath.Of(dir.Combine("NOTES")));
        Assert.Equal(canonical, CanonicalPath.Of(stored.ToLowerInvariant()));
        Assert.EndsWith(@"\Notes", canonical);
    }

    [Fact]
    public void Windows_resolves_a_junction_to_its_target()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var dir = new TempDirectory();
        var target = dir.Combine("target");
        Directory.CreateDirectory(target);
        var junction = dir.Combine("junction");
        // A junction, unlike a symlink, needs no privilege to create.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, target])
               {
                   RedirectStandardOutput = true,
               })!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.Equal(CanonicalPath.Of(target), CanonicalPath.Of(junction));
    }

    [Fact]
    public void Windows_resolves_a_short_name_to_the_long_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var dir = new TempDirectory();
        var longPath = dir.Combine("a folder with a long name");
        Directory.CreateDirectory(longPath);
        var shortPath = ShortPath(longPath);
        Assert.SkipWhen(Path.GetFileName(shortPath) == Path.GetFileName(longPath), "the volume keeps no 8.3 names");

        Assert.Equal(CanonicalPath.Of(longPath), CanonicalPath.Of(shortPath));
        Assert.EndsWith(@"\a folder with a long name", CanonicalPath.Of(shortPath));
    }

    [Fact]
    public void Windows_keeps_the_separator_of_a_drive_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Equal(root.ToUpperInvariant(), CanonicalPath.Of(root.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(@"\\?\C:\notes", @"C:\notes")]
    [InlineData(@"\\?\UNC\server\share\notes", @"\\server\share\notes")]
    [InlineData(@"\\?\unc\server\share", @"\\server\share")]
    [InlineData(@"C:\notes", @"C:\notes")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void A_final_path_loses_the_prefix_the_OS_adds(string finalPath, string expected) =>
        Assert.Equal(expected, CanonicalPath.WithoutPrefix(finalPath));

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathName(path, buffer, buffer.Length);
        Assert.True(length > 0 && length < buffer.Length, $"GetShortPathName failed: {Marshal.GetLastPInvokeError()}");
        return new string(buffer, 0, length);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, [Out] char[] shortPath, int bufferLength);
}
