using System.Reflection;

namespace Hippo.Tests.E2E;

public class ProcessTests
{
    [Fact]
    public async Task Version_flag_prints_the_version()
    {
        var expected = typeof(ProcessTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
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
