using Hippo.Commands.Self;

namespace Hippo.Tests.Unit;

/// <summary>The pieces of <c>self update</c> that decide without running anything: where the latest release is
/// looked up, what its redirect says, how the real client is set up, what a .NET tool is told, and that the binary
/// carries the install scripts as committed.</summary>
public class SelfUpdateTests
{
    private const string GitHub = "https://github.com/alisterpineda/hippo/releases/latest";

    [Theory]
    [InlineData(null, GitHub)]
    [InlineData("", GitHub)]
    [InlineData("http://127.0.0.1:1/releases/download", "http://127.0.0.1:1/releases/latest")]
    [InlineData("https://mirror.example/hippo/releases/download", "https://mirror.example/hippo/releases/latest")]
    [InlineData("http://127.0.0.1:1/releases/download/", GitHub)]
    [InlineData("http://127.0.0.1:1/somewhere", GitHub)]
    public void The_lookup_url_follows_the_download_base_only_when_it_ends_in_releases_download(string? downloadBase, string expected)
    {
        Assert.Equal(expected, LatestRelease.Url(downloadBase));
    }

    [Theory]
    [InlineData("https://github.com/alisterpineda/hippo/releases/tag/v0.3.0", "0.3.0")]
    [InlineData("/alisterpineda/hippo/releases/tag/v0.3.0-rc.1", "0.3.0-rc.1")]
    [InlineData("http://127.0.0.1:1/releases/tag/v1.0.0", "1.0.0")]
    [InlineData("https://github.com/alisterpineda/hippo/releases", null)]
    [InlineData("https://github.com/alisterpineda/hippo/releases/tag/v", null)]
    [InlineData("https://github.com/alisterpineda/hippo/releases/tag/vnope", null)]
    [InlineData("https://github.com/alisterpineda/hippo/releases/tag/0.3.0", null)]
    [InlineData("https://github.com/alisterpineda/hippo/releases/tag/v0.3.0/", null)]
    [InlineData(null, null)]
    public void The_version_is_read_from_a_redirect_ending_in_its_tag(string? location, string? expected)
    {
        var version = LatestRelease.VersionIn(location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute));

        Assert.Equal(expected, version?.ToString());
    }

    [Fact]
    public void The_real_handler_does_not_follow_redirects()
    {
        using var handler = LatestRelease.CreateHandler();

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public void The_real_client_times_out_and_names_hippo()
    {
        using var handler = new HttpClientHandler();
        using var client = LatestRelease.CreateClient(handler, SemVer.TryParse("0.2.0-dev")!);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.Equal("hippo/0.2.0-dev", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    /// <summary>The real handler refuses the scheme before any connection is made, so this reaches no network.</summary>
    [Fact]
    public void A_scheme_the_real_client_cannot_send_to_is_a_one_line_failure()
    {
        var result = LatestRelease.Find(LatestRelease.CreateHandler(), "ftp://127.0.0.1:9/releases/latest", SemVer.TryParse("0.2.0-dev")!);

        Assert.Null(result.Version);
        Assert.StartsWith("could not reach ftp://127.0.0.1:9/releases/latest: ", result.Failure);
        Assert.DoesNotContain("\n", result.Failure);
    }

    private static readonly SemVer V030 = SemVer.TryParse("0.3.0")!;

    private static SemVer V(string text) => SemVer.TryParse(text)!;

    [Fact]
    public void A_named_version_equal_to_the_current_one_is_already_installed()
    {
        var decision = SelfCommand.Decide(V030, named: V030, latest: null);

        Assert.Equal(new SelfCommand.Decision(null, "hippo 0.3.0 is already installed.", null), decision);
    }

    [Fact]
    public void A_named_newer_version_is_the_target()
    {
        var decision = SelfCommand.Decide(V030, named: V("0.4.0-rc.1"), latest: null);

        Assert.Equal(new SelfCommand.Decision(V("0.4.0-rc.1"), null, null), decision);
    }

    /// <summary>A step back warns about the index, and from before 0.2.0, that there is no way back but the script;
    /// the 0.2.0 prereleases have <c>self update</c>, so the boundary is on the numbers alone.</summary>
    [Theory]
    [InlineData("0.2.0", false)]
    [InlineData("0.2.0-rc.1", false)]
    [InlineData("0.2.5", false)]
    [InlineData("0.1.9", true)]
    [InlineData("0.1.0-rc.1", true)]
    public void A_named_older_version_is_the_target_with_a_warning(string named, bool noWayBack)
    {
        var decision = SelfCommand.Decide(V030, V(named), latest: null);

        Assert.Equal(V(named), decision.Target);
        Assert.Null(decision.Message);
        var expected = $"{named} is older than this hippo (0.3.0); it refuses an index this hippo upgraded until you delete that index's folder (hippo cache list shows where)"
            + (noWayBack ? $"; {named} has no self update; rerun the install script to come back" : "");
        Assert.Equal(expected, decision.Warning);
    }

    [Fact]
    public void The_latest_release_equal_to_the_current_one_is_already_installed()
    {
        var decision = SelfCommand.Decide(V030, named: null, latest: V030);

        Assert.Equal(new SelfCommand.Decision(null, "The latest release, 0.3.0, is already installed.", null), decision);
    }

    [Fact]
    public void A_newer_latest_release_is_the_target()
    {
        var decision = SelfCommand.Decide(V030, named: null, latest: V("0.3.1"));

        Assert.Equal(new SelfCommand.Decision(V("0.3.1"), null, null), decision);
    }

    [Theory]
    [InlineData("0.3.0-dev")]
    [InlineData("0.3.0-rc.2")]
    public void A_hippo_newer_than_the_latest_release_changes_nothing(string current)
    {
        var decision = SelfCommand.Decide(V(current), named: null, latest: V("0.2.9"));

        Assert.Equal(new SelfCommand.Decision(null, $"This hippo ({current}) is newer than the latest release (0.2.9); nothing changed. To install it anyway: hippo self update 0.2.9", null), decision);
    }

    [Fact]
    public void Deciding_needs_a_named_version_or_the_latest_release()
    {
        Assert.Throws<ArgumentNullException>(() => SelfCommand.Decide(V030, named: null, latest: null));
    }

    private static string Under(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    /// <summary>The variable the .NET CLI reads for the home folder on this OS, and the one it does not.</summary>
    private static readonly string HomeVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";

    private static readonly string OtherHomeVariable = OperatingSystem.IsWindows() ? "HOME" : "USERPROFILE";

    [Fact]
    public void A_tool_store_is_told_by_a_store_segment_above_the_binary()
    {
        var root = Under("tools");
        var baseDirectory = Path.Combine(root, ".store", "hippo", "0.2.0", "hippo.osx-arm64", "0.2.0", "tools", "any", "osx-arm64") + Path.DirectorySeparatorChar;

        Assert.Equal(root, ToolStore.InstallRoot(baseDirectory));
        Assert.Null(ToolStore.InstallRoot(Under("bin") + Path.DirectorySeparatorChar));
        Assert.Null(ToolStore.InstallRoot(Under("store", "bin")));
    }

    /// <summary>Only this OS's home variable counts: the other is set to a decoy, so reading the wrong one, or
    /// either, would name the decoy's store.</summary>
    [Fact]
    public void The_global_tool_store_is_updated_with_minus_g()
    {
        var home = Under("home");
        var variables = new Dictionary<string, string> { [HomeVariable] = home, [OtherHomeVariable] = Under("decoy") };

        var command = ToolStore.UpdateCommand(Path.Combine(home, ".dotnet", "tools"), name => variables.GetValueOrDefault(name));

        Assert.Equal("dotnet tool update -g hippo", command);
    }

    [Fact]
    public void The_other_OS_home_variable_does_not_name_the_global_tool_store()
    {
        var decoy = Under("decoy");
        var variables = new Dictionary<string, string> { [HomeVariable] = Under("home"), [OtherHomeVariable] = decoy };

        var command = ToolStore.UpdateCommand(Path.Combine(decoy, ".dotnet", "tools"), name => variables.GetValueOrDefault(name));

        Assert.Equal($"dotnet tool update hippo --tool-path {Path.Combine(decoy, ".dotnet", "tools")}", command);
    }

    [Fact]
    public void DOTNET_CLI_HOME_moves_the_global_tool_store()
    {
        var cliHome = Under("cli-home");
        var variables = new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = cliHome, [HomeVariable] = Under("home") };

        var command = ToolStore.UpdateCommand(Path.Combine(cliHome, ".dotnet", "tools"), name => variables.GetValueOrDefault(name));

        Assert.Equal("dotnet tool update -g hippo", command);
    }

    [Fact]
    public void Any_other_tool_store_is_updated_with_its_tool_path()
    {
        var root = Under("artifacts", "tool");
        var variables = new Dictionary<string, string> { [HomeVariable] = Under("home") };

        var command = ToolStore.UpdateCommand(root, name => variables.GetValueOrDefault(name));

        Assert.Equal($"dotnet tool update hippo --tool-path {root}", command);
    }

    /// <summary>The binary runs the scripts CI tested, so each embedded one is the committed file, byte for byte. The
    /// test project copies both from the repository root beside the test assembly.</summary>
    [Theory]
    [InlineData("install.sh")]
    [InlineData("install.ps1")]
    public void The_install_script_is_embedded_as_committed(string name)
    {
        var committed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name));

        var embedded = InstallScript.Bytes(name);

        Assert.True(committed.Length > 0);
        Assert.Equal(committed, embedded);
    }
}
