using System.Runtime.Versioning;
using System.Text.Json;

namespace Hippo.Tests.Integration;

public sealed class InitCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string Config => _workspace.Combine(Path.Combine(".hippo", "config.json"));

    [Fact]
    public void Init_leaves_an_existing_config_alone()
    {
        File.WriteAllText(Config, """{ "bundles": ["wiki"] }""");

        var result = _workspace.Run("init");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("already exists", result.Stderr);
        Assert.Equal("", result.Stdout);
        Assert.Equal("""{ "bundles": ["wiki"] }""", File.ReadAllText(Config));
    }

    [Fact]
    public void Init_inside_a_workspace_warns_that_the_outer_one_still_indexes_it()
    {
        Directory.CreateDirectory(_workspace.Combine("inner"));

        var result = _workspace.RunIn(_workspace.Combine("inner"), "init");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(_workspace.Combine("inner/.hippo/config.json")));
        Assert.Contains("warning", result.Stderr);
        Assert.Contains($"inside the workspace at {_workspace.Root},", result.Stderr);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public void Init_in_a_read_only_folder_reports_that_it_cannot_write()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var inner = _workspace.Combine("inner");
        Directory.CreateDirectory(inner);
        File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(inner, "probe");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Assert.Skip("this user can write to a read-only folder");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = _workspace.RunIn(inner, "init");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("cannot write", result.Stderr);
            Assert.Equal("", result.Stdout);
        }
        finally
        {
            File.SetUnixFileMode(inner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Init_writes_into_an_existing_hippo_folder_without_a_config()
    {
        File.Delete(Config);

        var result = _workspace.Run("init");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Config));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public void A_failed_init_leaves_a_hippo_folder_it_did_not_create()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var folder = _workspace.Combine(".hippo");
        File.Delete(Config);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(folder, "probe");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Assert.Skip("this user can write to a read-only folder");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = _workspace.Run("init");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("cannot write", result.Stderr);
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Outside_a_workspace_the_error_points_to_init()
    {
        File.Delete(Config);

        var result = _workspace.Run("status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hippo init", result.Stderr);
    }
}
