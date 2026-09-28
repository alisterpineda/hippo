namespace Hippo.Tests.Unit;

public class CliTests
{
    [Fact]
    public void Bare_command_prints_usage()
    {
        var output = new StringWriter();

        var exitCode = Cli.Build().Parse([]).Invoke(new() { Output = output });

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output.ToString());
        Assert.Contains("index", output.ToString());
    }

    [Fact]
    public void Version_is_the_package_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", Cli.Version);
    }
}
