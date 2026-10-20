using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using Hippo.Commands;
using Hippo.Commands.Self;

namespace Hippo.Tests.Integration;

/// <summary>
/// <c>self update</c> in-process, with hippo taken to be a file the test writes, the latest release answered by a
/// fake handler, and the install script run by a fake runner that records what it was given. Nothing reaches the
/// network or starts a process.
/// </summary>
public sealed class SelfUpdateCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    /// <summary>The folder hippo is taken to be in, holding the file hippo runs as.</summary>
    private readonly string _install;

    private ProcessStartInfo? _started;

    public SelfUpdateCommandTests()
    {
        _install = _workspace.Beside("install");
        Directory.CreateDirectory(_install);
        var binary = Path.Combine(_install, OperatingSystem.IsWindows() ? "hippo.exe" : "hippo");
        File.WriteAllText(binary, "");
        _workspace.ProcessPath = binary;
        _workspace.BaseDirectory = _install;
    }

    public void Dispose() => _workspace.Dispose();

    /// <summary>This hippo's version, as the command compares against it.</summary>
    private static readonly SemVer Current = SemVer.TryParse(Cli.Version.Split('+')[0])!;

    /// <summary>A release newer than this hippo, whatever its version.</summary>
    private static readonly string Newer = $"{Current.Major + 1}.0.0";

    /// <summary>A release older than this hippo, and from before <c>self</c> existed.</summary>
    private const string Older = "0.0.1";

    private void WriteReceipt(string json = """{"installedBy":"install.sh","version":"0.1.0"}""") =>
        File.WriteAllText(Path.Combine(_install, ".hippo-receipt.json"), json);

    /// <summary>Makes the runner record the script's start and exit with <paramref name="exitCode"/>.</summary>
    private void RecordTheScript(int exitCode = 0) => _workspace.RunProcess = start =>
    {
        _started = start;
        return exitCode;
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found) =>
        new(status) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    /// <summary>The variable the .NET CLI reads for the home folder on this OS.</summary>
    private static readonly string HomeVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";

    /// <summary>After a run, the install folder holds what it held before: the binary and the receipt, with no probe
    /// file left behind.</summary>
    private void AssertInstallFolderUntouched() =>
        Assert.Equal(
            new[] { ".hippo-receipt.json", Path.GetFileName(_workspace.ProcessPath!) }.Order(),
            Directory.GetFiles(_install).Select(Path.GetFileName).Order());

    /// <summary>Answers the lookup with a redirect to the tag of <paramref name="version"/>.</summary>
    private FakeHandler LatestIs(string version)
    {
        var handler = new FakeHandler(_ => Redirect($"https://github.com/alisterpineda/hippo/releases/tag/v{version}"));
        _workspace.HttpHandler = handler;
        return handler;
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Without_a_receipt_hippo_refuses_and_says_to_reinstall_with_the_script()
    {
        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var line = Assert.Single(Lines(result.Stderr));
        Assert.StartsWith("hippo: this hippo was not installed by the install script, so it cannot update itself; ", line);
        Assert.EndsWith($"reinstall it with the install script with HIPPO_INSTALL_DIR set to {_install}", line);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public void Without_a_receipt_under_the_global_tool_store_hippo_names_dotnet_tool_update_g()
    {
        var home = _workspace.Beside("home");
        _workspace.BaseDirectory = Path.Combine(home, ".dotnet", "tools", ".store", "hippo", "0.2.0", "hippo.osx-arm64", "0.2.0", "tools", "any", "osx-arm64") + Path.DirectorySeparatorChar;

        var result = _workspace.RunWith(new Dictionary<string, string> { [HomeVariable] = home }, "self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.EndsWith("; it is a .NET tool: update it with dotnet tool update -g hippo", Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void Without_a_receipt_under_another_tool_store_hippo_names_its_tool_path()
    {
        var tools = _workspace.Beside("tools");
        _workspace.BaseDirectory = Path.Combine(tools, ".store", "hippo", "0.2.0", "hippo.any", "0.2.0", "tools", "net10.0", "any") + Path.DirectorySeparatorChar;

        var result = _workspace.RunWith(new Dictionary<string, string> { [HomeVariable] = _workspace.Beside("home") }, "self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.EndsWith($"; it is a .NET tool: update it with dotnet tool update hippo --tool-path {tools}", Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_receipt_that_is_not_json_refuses()
    {
        WriteReceipt("install.sh wrote this");

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var line = Assert.Single(Lines(result.Stderr));
        Assert.StartsWith($"hippo: {Path.Combine(_install, ".hippo-receipt.json")} is not a receipt the install script wrote, so hippo cannot tell it installed this hippo and will not update it; ", line);
        Assert.Contains("reinstall it with the install script", line);
    }

    [Fact]
    public void A_receipt_from_an_unknown_installer_refuses_as_none_does()
    {
        WriteReceipt("""{"installedBy":"homebrew","version":"0.3.0"}""");

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.StartsWith("hippo: this hippo was not installed by the install script, so it cannot update itself; ", Assert.Single(Lines(result.Stderr)));
    }

    /// <summary>What the receipt contract lets a future installer write, and what a corrupt file looks like: each
    /// refuses in one line, as not a receipt or as an unknown installer, never as an unexpected error.</summary>
    [Theory]
    [InlineData("[]", "is not a receipt the install script wrote")]
    [InlineData("\"install.sh\"", "is not a receipt the install script wrote")]
    [InlineData("", "is not a receipt the install script wrote")]
    [InlineData("{}", "this hippo was not installed by the install script")]
    [InlineData("""{"installedBy":1}""", "this hippo was not installed by the install script")]
    [InlineData("""{"installedBy":null}""", "this hippo was not installed by the install script")]
    [InlineData("""{"installedBy":"Install.sh"}""", "this hippo was not installed by the install script")]
    [InlineData("""{"version":"0.3.0"}""", "this hippo was not installed by the install script")]
    public void A_receipt_no_script_wrote_refuses_in_one_line(string json, string expected)
    {
        WriteReceipt(json);

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var line = Assert.Single(Lines(result.Stderr));
        Assert.Contains(expected, line);
        Assert.DoesNotContain("unexpected", line);
    }

    [Fact]
    public void Fields_beside_installedBy_are_ignored()
    {
        WriteReceipt("""{"installedBy":"install.ps1","version":"0.1.0","addedToPath":true,"later":{"field":[1,2]}}""");
        LatestIs(Current.ToString());

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal($"The latest release, {Current}, is already installed.", result.Stdout.Trim());
    }

    [Fact]
    public void The_latest_release_already_installed_is_said_after_one_head_request()
    {
        WriteReceipt();
        var handler = LatestIs(Current.ToString());

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"The latest release, {Current}, is already installed.", result.Stdout.Trim());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.Equal("https://github.com/alisterpineda/hippo/releases/latest", request.RequestUri?.ToString());
        Assert.Equal($"hippo/{Current}", request.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public void A_relative_redirect_names_the_release_too(HttpStatusCode status)
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => Redirect($"/alisterpineda/hippo/releases/tag/v{Current}", status));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal($"The latest release, {Current}, is already installed.", result.Stdout.Trim());
    }

    [Fact]
    public void A_redirect_without_a_location_fails()
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal(
            "hippo: could not find the latest release: https://github.com/alisterpineda/hippo/releases/latest redirected without saying where; name a version: hippo self update <version>",
            Assert.Single(Lines(result.Stderr)));
    }

    /// <summary>curl in install.sh takes a base with no scheme; HttpClient refuses to send to one, before the handler
    /// runs, and that refusal is reported like any other failed lookup.</summary>
    [Fact]
    public void A_download_base_the_client_cannot_send_to_is_one_line()
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => throw new InvalidOperationException("the handler should not be reached"));

        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_DOWNLOAD_BASE"] = "mirror.example/releases/download" }, "self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var lines = Lines(result.Stderr);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("hippo: could not find the latest release: could not reach mirror.example/releases/latest: ", lines[1]);
        Assert.EndsWith("; name a version: hippo self update <version>", lines[1]);
    }

    [Fact]
    public void A_hippo_newer_than_the_latest_release_says_so_and_changes_nothing()
    {
        WriteReceipt();
        LatestIs(Older);

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"This hippo ({Current}) is newer than the latest release ({Older}); nothing changed. To install it anyway: hippo self update {Older}", result.Stdout.Trim());
        Assert.Null(_started);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    public void A_named_version_equal_to_this_one_needs_no_receipt_and_no_network(string prefix)
    {
        var result = _workspace.Run("self", "update", prefix + Current);

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"hippo {Current} is already installed.", result.Stdout.Trim());
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0.3.0+abc1234")]
    public void A_version_argument_that_names_no_release_is_a_usage_error(string argument)
    {
        var result = _workspace.Run("self", "update", argument);

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var lines = Lines(result.Stderr);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith($"'{argument}' ", lines[0]);
        Assert.Equal("Run 'hippo self update --help' for usage.", lines[1]);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public void No_redirect_fails_and_says_to_name_a_version()
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal(
            "hippo: could not find the latest release: https://github.com/alisterpineda/hippo/releases/latest answered HTTP 200 rather than a redirect to the release; name a version: hippo self update <version>",
            Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_redirect_to_something_other_than_a_release_fails()
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => Redirect("https://github.com/alisterpineda/hippo/releases"));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal(
            "hippo: could not find the latest release: https://github.com/alisterpineda/hippo/releases/latest redirected to https://github.com/alisterpineda/hippo/releases, which names no release; name a version: hippo self update <version>",
            Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_failed_request_is_one_line()
    {
        WriteReceipt();
        _workspace.HttpHandler = new FakeHandler(_ => throw new HttpRequestException("No such host is known"));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal(
            "hippo: could not find the latest release: could not reach https://github.com/alisterpineda/hippo/releases/latest: No such host is known; name a version: hippo self update <version>",
            Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_timeout_is_one_line()
    {
        WriteReceipt();
        // What HttpClient throws when its timeout passes.
        _workspace.HttpHandler = new FakeHandler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal(
            "hippo: could not find the latest release: no answer from https://github.com/alisterpineda/hippo/releases/latest within 30 s; name a version: hippo self update <version>",
            Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_runtime_that_cannot_say_where_hippo_is_exits_2()
    {
        _workspace.ProcessPath = null;

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal("hippo: cannot tell where this hippo is, so it cannot update itself", Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there: making a symlink needs a privilege.
    public void A_symlinked_hippo_updates_the_folder_it_points_into()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symlinks only");
        var real = _workspace.Beside("real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "hippo"), "");
        File.WriteAllText(Path.Combine(real, ".hippo-receipt.json"), """{"installedBy":"install.sh"}""");
        File.CreateSymbolicLink(Path.Combine(_install, "hippo-link"), Path.Combine(real, "hippo"));
        _workspace.ProcessPath = Path.Combine(_install, "hippo-link");
        RecordTheScript();

        var result = _workspace.Run("self", "update", Newer);

        Assert.True(result.ExitCode == ExitCode.Clean, result.Stderr);
        Assert.Equal(real, _started!.Environment["HIPPO_INSTALL_DIR"]);
    }

    [Fact]
    public void A_named_older_version_warns_and_goes_on()
    {
        WriteReceipt();
        RecordTheScript();
        // Below this hippo, whichever 0.2.0 or later version it is, and not from before self update existed.
        var target = "0.2.0-alpha";

        var result = _workspace.Run("self", "update", target);

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal(
            $"hippo: warning: {target} is older than this hippo ({Current}); it refuses an index this hippo upgraded until you delete that index's folder (hippo cache list shows where)",
            Assert.Single(Lines(result.Stderr)));
        Assert.Equal(target, _started!.Environment["HIPPO_VERSION"]);
    }

    [Fact]
    public void A_named_version_from_before_self_update_says_how_to_come_back()
    {
        WriteReceipt();
        RecordTheScript();

        var result = _workspace.Run("self", "update", "0.1.0");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.EndsWith("(hippo cache list shows where); 0.1.0 has no self update; rerun the install script to come back", Assert.Single(Lines(result.Stderr)));
        Assert.Equal("0.1.0", _started!.Environment["HIPPO_VERSION"]);
    }

    [Fact]
    public void The_script_is_written_as_embedded_under_its_own_name_and_run_from_its_folder()
    {
        WriteReceipt();
        LatestIs(Newer);
        string? name = null;
        byte[]? bytes = null;
        _workspace.RunProcess = start =>
        {
            _started = start;
            var script = start.ArgumentList[^1];
            name = Path.GetFileName(script);
            bytes = File.ReadAllBytes(script);
            Assert.Equal(Path.GetDirectoryName(script), start.WorkingDirectory);
            return 0;
        };

        var result = _workspace.Run("self", "update");

        Assert.True(result.ExitCode == ExitCode.Clean, result.Stderr);
        Assert.Equal(OperatingSystem.IsWindows() ? "install.ps1" : "install.sh", name);
        Assert.Equal(InstallScript.Bytes(name!), bytes);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), _started!.FileName);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], _started.ArgumentList.Take(5));
        }
        else
        {
            Assert.Equal("/bin/sh", _started!.FileName);
            Assert.Single(_started.ArgumentList);
        }
        Assert.False(_started.RedirectStandardOutput);
        Assert.False(_started.RedirectStandardError);
        Assert.False(_started.RedirectStandardInput);
        Assert.Equal(Newer, _started.Environment["HIPPO_VERSION"]);
        Assert.Equal(_install, _started.Environment["HIPPO_INSTALL_DIR"]);
        Assert.Equal("self-update", _started.Environment["HIPPO_CALLER"]);
        AssertInstallFolderUntouched();
    }

    [Fact]
    public void A_script_that_fails_makes_hippo_exit_2_with_nothing_added()
    {
        WriteReceipt();
        LatestIs(Newer);
        RecordTheScript(exitCode: 1);

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal("", result.Stdout);
        Assert.False(Directory.Exists(_started!.WorkingDirectory), "the script folder was left behind");
        AssertInstallFolderUntouched();
    }

    [Fact]
    public void A_script_that_cannot_start_is_reported_in_one_line()
    {
        WriteReceipt();
        LatestIs(Newer);
        string? folder = null;
        _workspace.RunProcess = start =>
        {
            folder = start.WorkingDirectory;
            throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
        };

        var result = _workspace.Run("self", "update");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        var line = Assert.Single(Lines(result.Stderr));
        Assert.StartsWith("hippo: cannot run ", line);
        Assert.EndsWith(": No such file or directory", line);
        Assert.False(Directory.Exists(folder), "the script folder was left behind");
        AssertInstallFolderUntouched();
    }

    [Fact]
    public void Hippo_sets_the_script_variables_over_those_in_its_environment()
    {
        WriteReceipt();
        LatestIs(Newer);
        RecordTheScript();
        var before = new[] { "HIPPO_VERSION", "HIPPO_INSTALL_DIR", "HIPPO_CALLER" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("HIPPO_VERSION", "0.0.0-wrong");
        Environment.SetEnvironmentVariable("HIPPO_INSTALL_DIR", _workspace.Beside("elsewhere"));
        Environment.SetEnvironmentVariable("HIPPO_CALLER", "someone-else");
        try
        {
            var result = _workspace.Run("self", "update");

            Assert.True(result.ExitCode == ExitCode.Clean, result.Stderr);
        }
        finally
        {
            foreach (var (name, value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        Assert.Equal(Newer, _started!.Environment["HIPPO_VERSION"]);
        Assert.Equal(_install, _started.Environment["HIPPO_INSTALL_DIR"]);
        Assert.Equal("self-update", _started.Environment["HIPPO_CALLER"]);
    }

    /// <summary>The line says where the lookup goes: with the base, when it ends as the scripts' download URL does,
    /// and on GitHub otherwise, as CI's own plain <c>http://127.0.0.1:8000</c> form would.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:1/releases/download", "http://127.0.0.1:1/releases/latest")]
    [InlineData("http://127.0.0.1:1", "https://github.com/alisterpineda/hippo/releases/latest")]
    public void A_download_base_override_is_announced_with_where_the_lookup_goes(string downloadBase, string lookup)
    {
        WriteReceipt();
        var handler = LatestIs(Current.ToString());

        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_DOWNLOAD_BASE"] = downloadBase }, "self", "update");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal(lookup, Assert.Single(handler.Requests).RequestUri?.ToString());
        Assert.Equal(
            $"hippo: warning: HIPPO_DOWNLOAD_BASE is set to {downloadBase}, so the release is downloaded from there, and the latest release is looked up at {lookup}",
            Assert.Single(Lines(result.Stderr)));
    }

    [Fact]
    public void A_download_base_override_with_a_named_version_mentions_no_lookup()
    {
        WriteReceipt();
        RecordTheScript();

        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_DOWNLOAD_BASE"] = "http://127.0.0.1:1" }, "self", "update", Newer);

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal("hippo: warning: HIPPO_DOWNLOAD_BASE is set to http://127.0.0.1:1, so the release is downloaded from there", Assert.Single(Lines(result.Stderr)));
        Assert.Equal(Newer, _started!.Environment["HIPPO_VERSION"]);
    }

    [Fact]
    public void The_script_folder_is_there_while_it_runs_and_gone_after()
    {
        WriteReceipt();
        LatestIs(Newer);
        string? folder = null;
        _workspace.RunProcess = start =>
        {
            folder = start.WorkingDirectory;
            Assert.True(Directory.Exists(folder));
            Assert.StartsWith("hippo-", Path.GetFileName(folder));
            return 0;
        };

        var result = _workspace.Run("self", "update");

        Assert.True(result.ExitCode == ExitCode.Clean, result.Stderr);
        Assert.NotNull(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public void A_folder_hippo_cannot_write_to_refuses_before_the_script_runs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        WriteReceipt();
        LatestIs(Newer);
        File.SetUnixFileMode(_install, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var probe = Path.Combine(_install, "probe");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Assert.Skip("this user can write to a read-only folder");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = _workspace.Run("self", "update");

            Assert.Equal(ExitCode.Error, result.ExitCode);
            Assert.Equal($"hippo: cannot write to {_install}, where this hippo is; run hippo self update as the user who installed it", Assert.Single(Lines(result.Stderr)));
            Assert.Null(_started);
        }
        finally
        {
            File.SetUnixFileMode(_install, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
