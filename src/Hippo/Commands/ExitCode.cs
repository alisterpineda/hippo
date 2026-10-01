namespace Hippo.Commands;

/// <summary>What hippo exits with.</summary>
internal static class ExitCode
{
    /// <summary>The command ran and has nothing to report.</summary>
    public const int Clean = 0;

    /// <summary>The command ran and reports findings, such as broken links or lint findings.</summary>
    public const int Findings = 1;

    /// <summary>The command could not run: a usage error, a <see cref="HippoException"/>, or any other failure.</summary>
    public const int Error = 2;
}
