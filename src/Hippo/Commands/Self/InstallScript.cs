using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hippo.Commands.Self;

/// <summary>
/// The install scripts, <c>install.sh</c> and <c>install.ps1</c>, embedded as committed (see <c>Hippo.csproj</c>),
/// and how <c>self update</c> runs the one for its OS: written to a private temp directory under its own name, and
/// started with an absolute interpreter, since a bare <c>sh</c> or <c>powershell.exe</c> is looked up in hippo's
/// working directory, inside a workspace that may be a cloned repository, before <c>PATH</c>.
/// </summary>
internal static class InstallScript
{
    public const string Caller = "self-update";

    /// <summary>The script for this OS.</summary>
    public static string Name => OperatingSystem.IsWindows() ? "install.ps1" : "install.sh";

    /// <summary>The embedded script <paramref name="name"/>, byte for byte.</summary>
    public static byte[] Bytes(string name)
    {
        using var resource = typeof(InstallScript).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in this build");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes this OS's script into <paramref name="directory"/>, a fresh temp directory, as a new file under
    /// its own name, since <c>-File</c> refuses one without <c>.ps1</c>; returns its path.</summary>
    public static string Write(string directory)
    {
        var path = Path.Combine(directory, Name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(Bytes(Name));
        return path;
    }

    /// <summary>How the script at <paramref name="script"/> runs: told the version and the directory, and that hippo is
    /// the caller, over whatever the environment holds; everything else, <c>HIPPO_DOWNLOAD_BASE</c> included, is
    /// inherited. Its output is not redirected, so the user sees the script's own lines.</summary>
    public static ProcessStartInfo StartInfo(string script, SemVer version, string installDirectory)
    {
        var start = new ProcessStartInfo { WorkingDirectory = Path.GetDirectoryName(script)!, UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            // Windows PowerShell 5.1 is on every Windows install. Client Windows defaults to the Restricted policy, which
            // blocks -File without Bypass; a Group Policy that pins the policy still blocks it, and that failure is the
            // script's to report.
            start.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add(script);
        }
        start.Environment["HIPPO_VERSION"] = version.ToString();
        start.Environment["HIPPO_INSTALL_DIR"] = installDirectory;
        start.Environment["HIPPO_CALLER"] = Caller;
        return start;
    }

    /// <summary>Runs <paramref name="start"/> to completion and returns its exit code; <see cref="Win32Exception"/>
    /// when it cannot start. While the child runs, an interrupt from the terminal (Ctrl+C, and Ctrl+\ or Ctrl+Break)
    /// is cancelled here: the child gets the same signal from the terminal itself and finishes its own cleanup, and
    /// hippo then removes the directory the script ran from.</summary>
    public static int Run(ProcessStartInfo start)
    {
        using var interrupts = IgnoreInterrupts();
        using var process = Process.Start(start) ?? throw new Win32Exception($"{start.FileName} did not start");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static IDisposable IgnoreInterrupts()
    {
        if (OperatingSystem.IsWindows())
        {
            ConsoleCancelEventHandler cancel = (_, e) => e.Cancel = true;
            Console.CancelKeyPress += cancel;
            return new Undo(() => Console.CancelKeyPress -= cancel);
        }
        var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context => context.Cancel = true);
        return new Undo(() =>
        {
            interrupt.Dispose();
            quit.Dispose();
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
