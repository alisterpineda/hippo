using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Hippo.Workspaces;

/// <summary>
/// The untracked files under the workspace root that git ignores, keyed like the index. git itself lists them, so every
/// source of its rules counts as git counts it: each <c>.gitignore</c>, <c>.git/info/exclude</c> and the user's global
/// excludes file. A tracked file is never in it, as git ignores only untracked files. Every file under one of
/// <see cref="Folders"/> is ignored.
/// </summary>
internal sealed record GitIgnored(IReadOnlySet<string> Folders, IReadOnlySet<string> Files)
{
    public static GitIgnored None { get; } = new(new HashSet<string>(), new HashSet<string>());

    // --directory names a folder whose files are all untracked and ignored as "folder/", so a large one such as
    // node_modules is one line. --no-empty-directory is left out: it also drops files that are listed one by one.
    // core.fsmonitor names a program for git to run, so a repository's own config must not set it for hippo.
    private static readonly string[] Arguments =
        ["-c", "core.fsmonitor=false", "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"];

    /// <summary>What git ignores under <paramref name="root"/>. None when no folder at or above the root holds a
    /// <c>.git</c>, so git is not run for a workspace outside a repository; when git cannot be run or fails, a warning
    /// says so and none.</summary>
    public static GitIgnored Find(string root, List<string> warnings)
    {
        if (!InRepository(root))
        {
            return None;
        }

        // A bare "git" would be looked up in hippo's working directory, inside the workspace, before PATH.
        if (FindGit(root) is not { } git)
        {
            warnings.Add("cannot run git, so the files it ignores are indexed; set files.gitignore to false to index them without this warning: git is not on PATH");
            return None;
        }

        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        string output, error;
        int exitCode;
        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            // Both streams are drained at once, so git never blocks on a full pipe that is not being read.
            var stderr = process.StandardError.ReadToEndAsync();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error = stderr.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            warnings.Add($"cannot run git, so the files it ignores are indexed; set files.gitignore to false to index them without this warning: {ex.Message}");
            return None;
        }

        if (exitCode != 0)
        {
            var reason = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"exit {exitCode}";
            warnings.Add($"git could not list the files it ignores, so they are indexed: {reason}");
            return None;
        }

        var folders = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (path == "./")
            {
                // The workspace root itself is ignored. Which entries git lists below it then depends on how the rule
                // is written, not on what is in the folder, so none of them count.
                return None;
            }
            if (path.EndsWith('/'))
            {
                folders.Add(path[..^1]);
            }
            else
            {
                files.Add(path);
            }
        }
        return new GitIgnored(folders, files);
    }

    /// <summary>The absolute path of git from the absolute folders on <c>PATH</c>, skipping any inside
    /// <paramref name="root"/> so a workspace cannot supply its own; null when there is none.</summary>
    private static string? FindGit(string root)
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }
            var candidate = Path.GetFullPath(Path.Combine(folder, name));
            if (candidate.StartsWith(inside, comparison) || !File.Exists(candidate))
            {
                continue;
            }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    /// <summary>Whether <paramref name="root"/> or a folder above it holds a <c>.git</c>: a folder, or the file that
    /// stands for one in a linked worktree or submodule.</summary>
    private static bool InRepository(string root)
    {
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            if (Path.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return true;
            }
        }
        return false;
    }
}
