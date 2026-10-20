using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hippo.Cache;
using Hippo.Commands.Self;

namespace Hippo;

/// <summary>
/// What a command reads from the process it runs in: the working directory, environment variables, the clock, the
/// mount points and whether standard output is a terminal. Tests pass their own to run commands in-process. The user
/// cache folder, used when <c>HIPPO_CACHE_DIR</c> is not set, comes from these variables too (<c>HOME</c>,
/// <c>LOCALAPPDATA</c>, <c>XDG_CACHE_HOME</c>); only when <c>HOME</c> or <c>LOCALAPPDATA</c> is unset or empty does
/// it fall back to the real process's. git stays with the real process: hippo finds it on the real <c>PATH</c> and runs
/// it with the real environment. What <c>self update</c> needs beyond these, where hippo is, how it looks the latest
/// release up and how it runs the install script, are properties with the real process's defaults, so the record's
/// callers did not change; the integration tests set all of them for every command, with a handler and a runner that
/// throw unless a test supplies its own.
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

    /// <summary>The file hippo runs as, with any symlink to it unresolved, or null when the runtime cannot tell.
    /// <c>self update</c> resolves it and updates the real file.</summary>
    public string? ProcessPath { get; init; } = Environment.ProcessPath;

    /// <summary>The directory hippo's code loads from: the binary's own, or for the framework-dependent .NET tool the
    /// one under <c>.store</c> that its shim loads. <c>self update</c> reads it only to word its refusal.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Makes the handler the latest-release lookup sends its one request through. The real one follows no
    /// redirects, since the redirect is the answer.</summary>
    public Func<HttpMessageHandler> CreateHttpHandler { get; init; } = LatestRelease.CreateHandler;

    /// <summary>Runs the install script as <c>self update</c> describes it and returns its exit code, throwing
    /// <see cref="System.ComponentModel.Win32Exception"/> when it cannot start.</summary>
    public Func<ProcessStartInfo, int> RunProcess { get; init; } = InstallScript.Run;

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
