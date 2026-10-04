using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hippo.Cache;

namespace Hippo;

/// <summary>
/// What a command reads from the process it runs in: the working directory, environment variables, the clock, the
/// mount points and whether standard output is a terminal. Tests pass their own to run commands in-process. Two reads
/// stay with the real process: the user cache folder, used when <c>HIPPO_CACHE_DIR</c> is not set, and git, which hippo
/// finds on the real <c>PATH</c> and runs with the real environment.
/// </summary>
/// <param name="OutputIsTerminal">Whether standard output is a terminal that shows ANSI styling. On Windows that is a
/// console with virtual-terminal processing on, which <see cref="Process"/> turns on.</param>
internal sealed partial record CliEnvironment(
    string WorkingDirectory, Func<string, string?> GetVariable, TimeProvider Clock, Func<IReadOnlyList<string>> GetMountPoints,
    bool OutputIsTerminal)
{
    public static CliEnvironment Process() =>
        new(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable, TimeProvider.System, MountPoints.Current,
            !Console.IsOutputRedirected && (!OperatingSystem.IsWindows() || ShowsStyling()));

    /// <summary>Whether text output may be styled: only on a terminal, and not when <c>NO_COLOR</c> is set to anything
    /// but the empty string (no-color.org).</summary>
    public bool Styled => OutputIsTerminal && string.IsNullOrEmpty(GetVariable("NO_COLOR"));

    private const int StdOutputHandle = -11;

    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>Whether the console standard output writes to shows ANSI styling, turning virtual-terminal processing on
    /// when it is off. The mode belongs to the console, not to hippo, so it stays on after hippo exits, as other tools
    /// leave it. False when the console cannot take it, such as on Windows before 10.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ShowsStyling()
    {
        var console = GetStdHandle(StdOutputHandle);
        return GetConsoleMode(console, out var mode)
            && ((mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(console, mode | EnableVirtualTerminalProcessing));
    }

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial nint GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint console, out uint mode);

    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint console, uint mode);
}
