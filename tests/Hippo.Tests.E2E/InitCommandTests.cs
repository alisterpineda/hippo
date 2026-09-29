using System.Runtime.Versioning;
using System.Text.Json;

namespace Hippo.Tests.E2E;

public sealed class InitCommandTests : IDisposable
{
    private readonly TempNotebook _notebook = new();

    public void Dispose() => _notebook.Dispose();

    private string Config => _notebook.Combine(".hippo.yaml");

    [Fact]
    public async Task Init_makes_the_folder_a_notebook()
    {
        File.Delete(Config);
        _notebook.Write("a.md", "# A\n");
        _notebook.Write(".git/HEAD", "ref: refs/heads/main\n");

        var init = await _notebook.RunAsync("init");
        var files = await _notebook.RunAsync("files", "--json");

        Assert.Equal(0, init.ExitCode);
        Assert.Contains(Config, init.Stdout);
        Assert.Equal("", init.Stderr);
        Assert.True(files.ExitCode == 0, $"exit {files.ExitCode}: {files.Stderr}");
        Assert.Equal("", files.Stderr);
        var paths = JsonDocument.Parse(files.Stdout).RootElement.EnumerateArray().Select(file => file.GetProperty("path").GetString());
        Assert.Equal([".hippo.yaml", "a.md"], paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Init_leaves_an_existing_config_alone()
    {
        File.WriteAllText(Config, "links: { roots: [\"index.md\"] }\n");

        var result = await _notebook.RunAsync("init");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("already exists", result.Stderr);
        Assert.Equal("", result.Stdout);
        Assert.Equal("links: { roots: [\"index.md\"] }\n", File.ReadAllText(Config));
    }

    [Fact]
    public async Task Init_inside_a_notebook_warns_that_the_outer_one_still_indexes_it()
    {
        Directory.CreateDirectory(_notebook.Combine("inner"));

        var result = await _notebook.RunInAsync(_notebook.Combine("inner"), "init");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(_notebook.Combine("inner/.hippo.yaml")));
        Assert.Contains("warning", result.Stderr);
        Assert.Contains($"inside the notebook at {_notebook.Root},", result.Stderr);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public async Task Init_in_a_read_only_folder_reports_that_it_cannot_write()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var inner = _notebook.Combine("inner");
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

            var result = await _notebook.RunInAsync(inner, "init");

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
    public async Task Outside_a_notebook_the_error_points_to_init()
    {
        File.Delete(Config);

        var result = await _notebook.RunAsync("status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hippo init", result.Stderr);
    }
}
