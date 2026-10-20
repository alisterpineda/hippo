using System.CommandLine;
using System.CommandLine.Help;
using System.ComponentModel;

namespace Hippo.Commands.Self;

/// <summary>
/// <c>hippo self update [&lt;version&gt;]</c>: updates this hippo when, and only when, an install script put it where it
/// is, by running the copy of that script built into the binary for its own directory. hippo decides the target
/// version and whether to install it; the script stays the one thing that downloads, checks and replaces the binary.
/// Like <c>init</c> and <c>cache</c>, it needs no workspace; like <c>init</c>, it has no <c>--json</c>, since it runs
/// an installer whose output is text. <see cref="Decide"/> is the version policy on its own, writing nothing, so it
/// is unit-tested; <see cref="Update"/> is the refusals and the side effects around it.
/// </summary>
internal static class SelfCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var command = new Command("self", "Update this hippo");
        command.SetAction(result => new HelpAction().Invoke(result));
        command.Subcommands.Add(BuildUpdate(environment));
        return command;
    }

    private static Command BuildUpdate(CliEnvironment environment)
    {
        var version = new Argument<string?>("version")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "The version to install, such as 0.2.0 or a prerelease such as 0.2.0-rc.1; the latest release otherwise",
        };
        version.Validators.Add(result =>
        {
            if (result.Tokens is [{ Value: var text }] && ParseTarget(text, out var problem) is null)
            {
                result.AddError(problem!);
            }
        });
        var command = new Command("update", "Update this hippo to the latest release, or to a named version, with its built-in install script") { version };
        command.SetAction(result => Guard.Run(result.InvocationConfiguration.Error, () =>
            Update(environment, result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, result.GetValue(version))));
        return command;
    }

    /// <summary>This hippo's version, without the commit <see cref="Cli.Version"/> carries as build metadata.</summary>
    private static SemVer Current =>
        SemVer.TryParse(Cli.Version.Split('+')[0]) ?? throw new InvalidOperationException($"{Cli.Version} is not a version");

    /// <summary>The version the user named, with a leading <c>v</c> stripped; null, with <paramref name="problem"/> set,
    /// when it is not a version or carries build metadata, which names no release: the script would build an asset
    /// name no release has.</summary>
    private static SemVer? ParseTarget(string text, out string? problem)
    {
        var version = SemVer.TryParse(text.StartsWith('v') ? text[1..] : text);
        if (version is null)
        {
            problem = $"'{text}' is not a version, such as 0.2.0 or 0.2.0-rc.1";
            return null;
        }
        if (version.BuildMetadata.Length > 0)
        {
            problem = $"'{text}' has build metadata after the +, which no release carries; name the version alone: {version.WithoutMetadata()}";
            return null;
        }
        problem = null;
        return version;
    }

    private static int Update(CliEnvironment environment, TextWriter output, TextWriter error, string? argument)
    {
        var current = Current;
        var named = argument is null ? null : ParseTarget(argument, out _);
        // A named version is decided before anything is read, so naming the installed one needs no receipt and no network.
        var decision = named is null ? null : Decide(current, named, latest: null);
        if (decision?.Message is { } early)
        {
            output.WriteLine(early);
            return ExitCode.Clean;
        }

        // The real file: a script install symlinked from elsewhere updates where the symlink points.
        if (environment.ProcessPath is not { } rawPath)
        {
            throw new HippoException("cannot tell where this hippo is, so it cannot update itself");
        }
        var path = File.ResolveLinkTarget(rawPath, returnFinalTarget: true)?.FullName ?? rawPath;
        var directory = Path.GetDirectoryName(path) ?? throw new HippoException($"cannot tell what folder {path} is in, so hippo cannot update itself");

        if (Receipt.Read(directory) is var receipt && receipt != ReceiptState.Found)
        {
            throw new HippoException(Refusal(receipt, directory, environment));
        }

        // With the base overridden, install.sh drops its HTTPS-only flags and the checksum comes from the same place,
        // so the user should see the override is in force. The script reads the variable itself; the lookup moves
        // with it only by LatestRelease.Url's rule, so the line says where the lookup goes.
        var downloadBase = environment.GetVariable("HIPPO_DOWNLOAD_BASE");
        var lookupUrl = LatestRelease.Url(downloadBase);
        if (!string.IsNullOrEmpty(downloadBase))
        {
            WorkspaceSession.Warn(error, [$"HIPPO_DOWNLOAD_BASE is set to {downloadBase}, so the release is downloaded from there"
                + (named is null ? $", and the latest release is looked up at {lookupUrl}" : "")]);
        }

        if (decision is null)
        {
            var latest = LatestRelease.Find(environment.CreateHttpHandler(), lookupUrl, current);
            if (latest.Version is null)
            {
                throw new HippoException($"could not find the latest release: {latest.Failure}; name a version: hippo self update <version>");
            }
            decision = Decide(current, named: null, latest.Version);
            if (decision.Message is { } message)
            {
                output.WriteLine(message);
                return ExitCode.Clean;
            }
        }
        if (decision.Warning is { } warning)
        {
            WorkspaceSession.Warn(error, [warning]);
        }
        var target = decision.Target!;

        EnsureWritable(directory);

        var temp = Directory.CreateTempSubdirectory("hippo-");
        try
        {
            var start = InstallScript.StartInfo(InstallScript.Write(temp.FullName), target, directory);
            int exitCode;
            try
            {
                exitCode = environment.RunProcess(start);
            }
            catch (Win32Exception ex)
            {
                throw new HippoException($"cannot run {start.FileName}: {ex.Message}");
            }
            // The script printed why it failed; nothing to add.
            return exitCode == 0 ? ExitCode.Clean : ExitCode.Error;
        }
        finally
        {
            try
            {
                temp.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A script that left something open keeps a few KB in the temp folder; not worth a message.
            }
        }
    }

    /// <summary>What <see cref="Decide"/> settled on: a <see cref="Message"/> to print when nothing is to be
    /// installed, else the <see cref="Target"/> to install, with a <see cref="Warning"/> when it is a step back.</summary>
    internal sealed record Decision(SemVer? Target, string? Message, string? Warning);

    /// <summary>The version policy, from the versions alone. <paramref name="named"/> is the version the user named;
    /// without one, <paramref name="latest"/> is the latest release, and one of the two must be given. The installed
    /// version is reported as already there. Without a name, an older latest release, which a prerelease or dev build
    /// is newer than, changes nothing: going back is a choice to make on purpose. A named older version is installed
    /// with a warning, since an older hippo refuses an index this one upgraded, and one from before 0.2.0 has no
    /// <c>self update</c> to come back with.</summary>
    internal static Decision Decide(SemVer current, SemVer? named, SemVer? latest)
    {
        var target = named ?? latest ?? throw new ArgumentNullException(nameof(latest), "Without a named version the latest release is needed.");
        var order = target.CompareTo(current);
        if (order == 0)
        {
            return new Decision(null, named is null ? $"The latest release, {target}, is already installed." : $"hippo {target} is already installed.", null);
        }
        if (order > 0)
        {
            return new Decision(target, null, null);
        }
        if (named is null)
        {
            return new Decision(null, $"This hippo ({current}) is newer than the latest release ({target}); nothing changed. To install it anyway: hippo self update {target}", null);
        }
        var warning = $"{target} is older than this hippo ({current}); it refuses an index this hippo upgraded until you delete that index's folder (hippo cache list shows where)";
        if ((target.Major, target.Minor, target.Patch) is (0, < 2, _))
        {
            warning += $"; {target} has no self update; rerun the install script to come back";
        }
        return new Decision(target, null, warning);
    }

    /// <summary>Why hippo will not update itself without a receipt, and what to do instead: a .NET tool is updated
    /// with the CLI, anything else is reinstalled with the script so that <c>self update</c> can manage it. The base
    /// directory, not the process path: for the framework-dependent tool the shim loads hippo from under
    /// <c>.store</c>.</summary>
    private static string Refusal(ReceiptState receipt, string directory, CliEnvironment environment)
    {
        var reason = receipt == ReceiptState.Unreadable
            ? $"{Path.Combine(directory, Receipt.FileName)} is not a receipt the install script wrote, so hippo cannot tell it installed this hippo and will not update it"
            : "this hippo was not installed by the install script, so it cannot update itself";
        var advice = ToolStore.InstallRoot(environment.BaseDirectory) is { } root
            ? $"it is a .NET tool: update it with {ToolStore.UpdateCommand(root, environment.GetVariable)}"
            : $"to let self update manage it, reinstall it with the install script with HIPPO_INSTALL_DIR set to {directory}";
        return $"{reason}; {advice}";
    }

    /// <summary>Fails before anything is downloaded when the script could not replace the binary anyway.</summary>
    private static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, $".hippo-self-update.{Environment.ProcessId}");
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HippoException($"cannot write to {directory}, where this hippo is; run hippo self update as the user who installed it");
        }
    }
}
