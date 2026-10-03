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
            ["init", "index", "status", "find", "show", "refs", "backrefs", "lint", "cache"],
            Cli.Build().Subcommands.Select(c => c.Name));
    }

    [Fact]
    public void Version_is_the_package_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", Cli.Version);
    }

    /// <summary>Each command, then arguments it refuses, and the start of the error. <c>find</c> takes a lone
    /// <c>--no-such-option</c> as its query, so it is given a bad limit.</summary>
    [Theory]
    [InlineData("", "--no-such-option", "Unrecognized command or argument '--no-such-option'")]
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
        Assert.DoesNotContain("Usage:", output.ToString());
    }
}
