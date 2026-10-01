using System.Text.Json;
using Hippo.Commands;

namespace Hippo.Tests.Integration;

/// <summary>Commands read the working directory, environment and clock they are given, never the test process's. The
/// other integration tests rely on that too; these check what they cannot see.</summary>
public sealed class CliEnvironmentTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == ExitCode.Clean, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    [Fact]
    public void Outside_any_workspace_is_an_error_naming_the_given_directory()
    {
        using var elsewhere = new TempDirectory();

        var result = _workspace.RunIn(elsewhere.FullPath, "find");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Contains($"no .hippo/config.json in {elsewhere.FullPath} or any folder above it", result.Stderr);
    }

    [Fact]
    public void The_sweep_reads_the_given_clock()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow.AddHours(1));
        _workspace.Clock = clock;

        var json = Json(_workspace.Run("status", "--json"));

        Assert.Equal(clock.Now, json.GetProperty("lastSweep").GetProperty("finishedAt").GetDateTimeOffset());
    }
}
