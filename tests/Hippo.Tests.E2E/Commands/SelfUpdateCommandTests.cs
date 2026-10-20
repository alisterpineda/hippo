using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Hippo.Tests.E2E.Commands;

/// <summary>
/// <c>self update</c> under the real binary. The smoke test and the refusal run against whichever binary the suite
/// runs against; the whole path, which installs over a copy of the binary from a loopback release, runs against the
/// native publish only, so <see cref="CommandCoverageTests"/> sees it skipped under <c>HIPPO_EXE</c>.
/// </summary>
public sealed class SelfUpdateCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>With the base pointed at a closed port, any request fails at once rather than reaching GitHub.</summary>
    private static readonly Dictionary<string, string> NoNetwork = new() { ["HIPPO_DOWNLOAD_BASE"] = "http://127.0.0.1:9/releases/download" };

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>This binary's version without the commit it carries after the <c>+</c>.</summary>
    private static async Task<string> CurrentVersionAsync()
    {
        var result = await HippoProcess.RunAsync("--version");
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return result.Stdout.Trim().Split('+')[0];
    }

    /// <summary><c>self update</c> has no <c>--json</c>, so like <c>init</c>'s its smoke test checks the exit code and
    /// stderr. Naming this hippo's own version ends the command before it reads a receipt or asks for the latest
    /// release, so this holds under every binary the suite runs against.</summary>
    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public async Task Self_update()
    {
        var current = await CurrentVersionAsync();

        var result = await _workspace.RunWithAsync(NoNetwork, "self", "update", current);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"hippo {current} is already installed.", result.Stdout.Trim());
    }

    /// <summary>No binary the suite runs against has a receipt beside it. Which advice hippo gives is told here from
    /// the binary's path, as hippo tells it from its own: a <c>.store</c> folder above the binary, or beside the shim
    /// that loads it, means a .NET tool; the native publish gets the generic advice.</summary>
    [Fact]
    public async Task Without_a_receipt_hippo_refuses_with_advice_for_what_it_is()
    {
        var exe = Path.GetFullPath(HippoProcess.ExePath);
        var expected = ToolRoot(exe) is { } root
            ? $"it is a .NET tool: update it with {(IsGlobalToolRoot(root) ? "dotnet tool update -g hippo" : $"dotnet tool update hippo --tool-path {root}")}"
            : $"reinstall it with the install script with HIPPO_INSTALL_DIR set to {Path.GetDirectoryName(exe)}";

        var result = await _workspace.RunWithAsync(NoNetwork, "self", "update");

        Assert.Equal(2, result.ExitCode);
        var line = Assert.Single(Lines(result.Stderr));
        Assert.StartsWith("hippo: this hippo was not installed by the install script, so it cannot update itself; ", line);
        Assert.EndsWith(expected, line, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal("", result.Stdout);
    }

    /// <summary>The folder a .NET tool was installed to, when <paramref name="exe"/> is one: the one above a
    /// <c>.store</c> segment, or the shim's own when a <c>.store</c> sits beside it.</summary>
    private static string? ToolRoot(string exe)
    {
        for (var folder = new DirectoryInfo(exe).Parent; folder is not null; folder = folder.Parent)
        {
            if (folder.Name == ".store")
            {
                return folder.Parent?.FullName;
            }
        }
        var beside = Path.GetDirectoryName(exe)!;
        return Directory.Exists(Path.Combine(beside, ".store")) ? beside : null;
    }

    private static bool IsGlobalToolRoot(string root)
    {
        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.Combine(home, ".dotnet", "tools")), comparison);
    }

    /// <summary>
    /// The whole path, with the script the binary carries: a copy of the binary with a receipt beside it, a release one
    /// major version up served from a loopback port, and <c>hippo self update</c> with no argument. The archive holds
    /// the same bytes, since macOS checks the binary's signature, under a fixed mtime the copy does not have, which is
    /// how the test tells the installed file was replaced. The variables hippo overrides for the script are set to
    /// wrong values, so the test sees hippo's win.
    /// </summary>
    [Fact]
    public async Task Self_update_installs_a_newer_release_with_its_built_in_script()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HIPPO_EXE")), "runs against the native publish only");
        var current = await CurrentVersionAsync();
        var newer = $"{int.Parse(current.Split('.')[0]) + 1}.0.0";
        var exeName = OperatingSystem.IsWindows() ? "hippo.exe" : "hippo";

        using var folder = new TempDirectory();
        var install = folder.Combine("bin");
        Directory.CreateDirectory(install);
        var binary = Path.Combine(install, exeName);
        File.Copy(HippoProcess.ExePath, binary);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.SetLastWriteTimeUtc(binary, DateTime.UtcNow);
        var receipt = Path.Combine(install, ".hippo-receipt.json");
        File.WriteAllText(receipt, OperatingSystem.IsWindows()
            ? """{"installedBy":"install.ps1","version":"0.1.0","addedToPath":false}"""
            : """{"installedBy":"install.sh","version":"0.1.0"}""");

        // What the script will pick for this machine, which the runtime names the same way.
        var rid = RuntimeInformation.RuntimeIdentifier;
        var archived = new DateTimeOffset(new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Local));
        var bytes = File.ReadAllBytes(HippoProcess.ExePath);
        var asset = OperatingSystem.IsWindows() ? $"hippo-{newer}-{rid}.zip" : $"hippo-{newer}-{rid}.tar.gz";
        var archive = OperatingSystem.IsWindows() ? Zip(exeName, bytes, archived) : TarGz(exeName, bytes, archived);
        using var server = new ReleaseServer(newer);
        server.Add(newer, asset, archive);
        server.Add(newer, "SHA256SUMS", Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {asset}\n"));

        var userPathBefore = OperatingSystem.IsWindows() ? UserPath() : null;
        var elsewhere = folder.Combine("elsewhere");
        var environment = new Dictionary<string, string>
        {
            ["HIPPO_DOWNLOAD_BASE"] = server.DownloadBase,
            ["HIPPO_VERSION"] = "0.0.0-wrong",
            ["HIPPO_INSTALL_DIR"] = elsewhere,
        };

        var result = await HippoProcess.RunExeAsync(binary, folder.FullPath, environment, "self", "update");

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}\n{result.Stdout}");
        Assert.Contains("Installed hippo", result.Stdout);
        Assert.DoesNotContain("PATH", result.Stderr);
        Assert.False(Directory.Exists(elsewhere), "the script installed where HIPPO_INSTALL_DIR in the environment said");
        // Zip entries keep a time to two seconds.
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(binary) - archived.UtcDateTime).TotalSeconds) <= 2, $"mtime {File.GetLastWriteTimeUtc(binary):O}, not the archive's {archived.UtcDateTime:O}");
        Assert.Contains($"\"version\":\"{newer}\"", File.ReadAllText(receipt));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(binary + ".old"), "the running hippo.exe was not moved aside");
            Assert.Equal(userPathBefore, UserPath());
        }
        var installed = await HippoProcess.RunExeAsync(binary, folder.FullPath, new Dictionary<string, string>(), "--version");
        Assert.Equal(0, installed.ExitCode);
        Assert.Equal(current, installed.Stdout.Trim().Split('+')[0]);
    }

    private static byte[] TarGz(string name, byte[] bytes, DateTimeOffset modified)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                ModificationTime = modified,
            });
        }
        return output.ToArray();
    }

    private static byte[] Zip(string name, byte[] bytes, DateTimeOffset modified)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name);
            entry.LastWriteTime = modified;
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        return output.ToArray();
    }

    /// <summary>The user <c>PATH</c> as stored, which <c>install.ps1</c> must leave alone under <c>hippo self update</c>.</summary>
    [SupportedOSPlatform("windows")]
    private static string UserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }
}
