namespace Hippo.Tests;

public class ProcessTests
{
    [Fact]
    public async Task Bare_command_prints_the_skeleton_message()
    {
        var result = await HippoProcess.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"hippo {Cli.Version}: nothing indexed yet", result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Version_flag_prints_the_version()
    {
        var result = await HippoProcess.RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Cli.Version, result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Help_flag_prints_usage()
    {
        var result = await HippoProcess.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.Stdout);
        Assert.Contains("hippo", result.Stdout);
    }

    [Fact]
    public async Task Unknown_argument_fails()
    {
        var result = await HippoProcess.RunAsync("bogus");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unrecognized command or argument 'bogus'", result.Stderr);
        Assert.DoesNotContain("nothing indexed yet", result.Stdout);
    }
}
