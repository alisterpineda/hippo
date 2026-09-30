using System.Text.Json;

namespace Hippo.Tests.E2E;

public sealed class InitCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private string Config => _workspace.Combine(Path.Combine(".hippo", "config.json"));

    [Fact]
    public async Task Init_makes_the_folder_a_workspace()
    {
        Directory.Delete(_workspace.Combine(".hippo"), recursive: true);
        _workspace.Write("a.md", "# A\n");
        _workspace.Git("init");

        var init = await _workspace.RunAsync("init");
        var files = await _workspace.RunAsync("files", "--json");

        Assert.Equal(0, init.ExitCode);
        Assert.Contains(Config, init.Stdout);
        Assert.Equal("", init.Stderr);
        Assert.True(files.ExitCode == 0, $"exit {files.ExitCode}: {files.Stderr}");
        Assert.Equal("", files.Stderr);
        var paths = JsonDocument.Parse(files.Stdout).RootElement.EnumerateArray().Select(file => file.GetProperty("path").GetString());
        Assert.Equal(["a.md"], paths.Order(StringComparer.Ordinal));
    }
}
