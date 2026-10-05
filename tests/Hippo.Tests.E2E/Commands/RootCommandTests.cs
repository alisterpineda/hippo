using System.Reflection;

namespace Hippo.Tests.E2E.Commands;

/// <summary>The flags of <c>hippo</c> itself, which only the real process answers: in-process, <c>--version</c>
/// reports the test exe's version and the help names the test host.</summary>
public class RootCommandTests
{
    [Fact]
    public async Task Version_flag_prints_the_version()
    {
        var expected = typeof(RootCommandTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HippoVersion").Value;

        var result = await HippoProcess.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Help_flag_prints_usage()
    {
        var result = await HippoProcess.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.Stdout);
        Assert.Contains("hippo", result.Stdout);
    }
}
