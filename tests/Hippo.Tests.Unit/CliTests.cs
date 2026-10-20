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
    public void The_commands_are_listed_in_order()
    {
        Assert.Equal(
            ["init", "index", "status", "find", "show", "refs", "backrefs", "lint", "cache", "self"],
            Cli.Build().Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void Version_is_the_package_version_and_commit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\+[0-9a-f]{7}$", Cli.Version);
    }

    /// <summary>Each command, then arguments it refuses, and the start of the error. <c>find</c> takes a lone
    /// <c>--no-such-option</c> as its query, so it is given a bad limit; <c>self update</c> likewise takes one as its
    /// version, so it is given what is not a version, and a version with build metadata.</summary>
    [Theory]
    [InlineData("", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("", "bogus", "Unrecognized command or argument 'bogus'")]
    [InlineData("init", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("index", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("status", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("find", "--limit nope", "Cannot parse argument 'nope' for option '--limit'")]
    [InlineData("show", "x.md --no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("refs", "x.md --no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("backrefs", "x.md --no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("lint", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("cache", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("cache list", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("cache prune", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("self", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
    [InlineData("self update", "--no-such-option", "'--no-such-option' is not a version, such as 0.2.0 or 0.2.0-rc.1")]
    [InlineData("self update", "nope", "'nope' is not a version, such as 0.2.0 or 0.2.0-rc.1")]
    [InlineData("self update", "1.2", "'1.2' is not a version, such as 0.2.0 or 0.2.0-rc.1")]
    [InlineData("self update", "v0.3.0+abc1234", "'v0.3.0+abc1234' has build metadata after the +, which no release carries; name the version alone: 0.3.0")]
    public void A_usage_error_prints_the_error_and_a_pointer_to_help_and_exits_2(string command, string args, string expected)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] words = [.. command.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

        var exitCode = Cli.Run([.. words, .. args.Split(' ')], new() { Output = output, Error = error });

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, exitCode);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(expected, lines[0]);
        Assert.Equal($"Run '{string.Join(' ', ["hippo", .. words])} --help' for usage.", lines[1]);
        Assert.Empty(output.ToString().Trim());
    }
}
