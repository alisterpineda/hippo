namespace Hippo.Tests.Unit;

public class CliTests
{
    [Fact]
    public void Bare_command_prints_the_skeleton_message()
    {
        var output = new StringWriter();

        var exitCode = Cli.Build().Parse([]).Invoke(new() { Output = output });

        Assert.Equal(0, exitCode);
        Assert.Equal($"hippo {Cli.Version}: nothing indexed yet", output.ToString().TrimEnd());
    }

    [Fact]
    public void Version_is_the_package_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", Cli.Version);
    }
}
