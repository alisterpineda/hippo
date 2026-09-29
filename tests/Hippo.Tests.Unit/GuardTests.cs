using Hippo.Commands;

namespace Hippo.Tests.Unit;

public class GuardTests
{
    [Fact]
    public void A_command_that_runs_returns_its_own_exit_code()
    {
        var error = new StringWriter();

        var exitCode = Guard.Run(error, () => ExitCode.Findings);

        Assert.Equal(ExitCode.Findings, exitCode);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void A_hippo_exception_prints_its_message_escaped()
    {
        var error = new StringWriter();

        var exitCode = Guard.Run(error, () => throw new HippoException("n\u001b]0;T\u0007.md is not in the index"));

        Assert.Equal(ExitCode.Error, exitCode);
        Assert.Equal("hippo: n\\x1b]0;T\\x07.md is not in the index" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void An_unexpected_error_prints_whole_with_each_line_escaped()
    {
        var error = new StringWriter();

        var exitCode = Guard.Run(error, () => throw new InvalidOperationException("cannot open n\u001b.md"));

        Assert.Equal(ExitCode.Error, exitCode);
        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("hippo: unexpected error: System.InvalidOperationException: cannot open n\\x1b.md", lines[0]);
        Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', error.ToString());
    }

    [Fact]
    public void An_unexpected_errors_inner_exceptions_print_escaped_after_it()
    {
        var error = new StringWriter();

        Guard.Run(error, () => throw new InvalidOperationException("outer", new IOException("cannot open n\u001b\n.md")));

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("hippo: unexpected error: System.InvalidOperationException: outer", lines[0]);
        Assert.Contains(" ---> System.IO.IOException: cannot open n\\x1b\\x0a.md", lines);
        Assert.DoesNotContain('\u001b', error.ToString());
    }

    [Fact]
    public void A_line_break_in_an_unexpected_errors_message_cannot_start_a_line_of_its_own()
    {
        var error = new StringWriter();

        Guard.Run(error, () => throw new InvalidOperationException("cannot open a\nhippo: warning: forged.md"));

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "hippo: unexpected error: System.InvalidOperationException: cannot open a\\x0ahippo: warning: forged.md",
            lines[0]);
        Assert.DoesNotContain(lines, line => line.StartsWith("hippo: warning", StringComparison.Ordinal));
    }
}
