namespace Hippo.Commands;

/// <summary>Where every command's failures end, so each prints the same way.</summary>
internal static class Guard
{
    /// <summary>Runs <paramref name="command"/>. A <see cref="HippoException"/> prints its message to
    /// <paramref name="error"/>, any other failure prints whole, and both exit <see cref="ExitCode.Error"/>. Either can
    /// quote a path or value from the workspace, so both are escaped.</summary>
    public static int Run(TextWriter error, Func<int> command)
    {
        try
        {
            return command();
        }
        catch (HippoException ex)
        {
            error.WriteLine($"hippo: {Format.Safe(ex.Message)}");
            return ExitCode.Error;
        }
        catch (Exception ex)
        {
            error.WriteLine($"hippo: unexpected error: {Describe(ex)}");
            return ExitCode.Error;
        }
    }

    /// <summary>Each exception in the chain as its type and message, then its stack trace. A message is escaped whole,
    /// so a line break quoted from the workspace prints as <c>\x0a</c> rather than starting a line of its own; only the
    /// stack trace keeps its line breaks.</summary>
    private static string Describe(Exception ex)
    {
        var lines = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            lines.Add((e == ex ? "" : " ---> ") + Format.Safe($"{e.GetType()}: {e.Message}"));
            if (e.StackTrace is { } trace)
            {
                lines.Add(Format.SafeLines(trace));
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
