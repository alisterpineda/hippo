using System.Reflection;
using System.Text.RegularExpressions;

namespace Hippo.Tests.E2E.Commands;

/// <summary>The flags of <c>hippo</c> itself, which only the real process answers: in-process, <c>--version</c>
/// reports the test exe's version and the help names the test host.</summary>
public class RootCommandTests
{
    [Fact]
    public async Task Version_flag_prints_the_version_and_commit()
    {
        var expected = typeof(RootCommandTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "HippoVersion").Value!;

        var result = await HippoProcess.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches($@"^{Regex.Escape(expected)}\+[0-9a-f]{{7}}$", result.Stdout.TrimEnd());
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
