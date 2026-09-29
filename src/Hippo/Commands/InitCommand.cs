using System.CommandLine;
using Hippo.Workspaces;

namespace Hippo.Commands;

/// <summary>Makes the working directory a workspace by writing a starter <c>.hippo/config.json</c>. Unlike the other commands
/// it needs no workspace, so it does not open a <see cref="WorkspaceSession"/>.</summary>
internal static class InitCommand
{
    public static Command Build()
    {
        var command = new Command("init", $"Make the current folder a workspace by writing a starter {WorkspaceConfig.RelativePath}");
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            try
            {
                var directory = Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory());
                var path = WorkspaceConfig.PathIn(directory);
                // Found before the config is written, so it is the enclosing workspace, if any, and never this folder.
                var enclosing = Workspace.FindRoot(directory);
                Write(path);

                if (enclosing is not null)
                {
                    error.WriteLine($"hippo: warning: {Format.Safe(directory)} is inside the workspace at {Format.Safe(enclosing)}, " +
                        "which may still index its files; exclude this folder there to keep them apart");
                }
                output.WriteLine($"Created {Format.Safe(path)}. Edit it to choose which files are indexed, then run hippo index.");
                return 0;
            }
            catch (HippoException ex)
            {
                error.WriteLine($"hippo: {Format.Safe(ex.Message)}");
                return 2;
            }
            catch (Exception ex)
            {
                error.WriteLine($"hippo: unexpected error: {ex}");
                return 2;
            }
        });
        return command;
    }

    /// <summary>Writes the starter config to <paramref name="path"/>, creating its folder if need be, never over an
    /// existing file, and removes what it created if the write fails partway so a rerun is not refused.</summary>
    private static void Write(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var createdFolder = !Directory.Exists(folder);
        FileStream stream;
        try
        {
            Directory.CreateDirectory(folder);
            // CreateNew is the existence check: an existing config, even one written a moment ago, is never overwritten.
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new HippoException($"{path} already exists");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RemoveFolder(folder, createdFolder);
            throw new HippoException($"cannot write {path}: {ex.Message}");
        }

        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(WorkspaceConfig.Starter);
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The write error below is the one worth reporting.
            }
            RemoveFolder(folder, createdFolder);
            throw new HippoException($"cannot write {path}: {ex.Message}");
        }
    }

    /// <summary>Removes <paramref name="folder"/> when this run <paramref name="created"/> it and it is still empty.</summary>
    private static void RemoveFolder(string folder, bool created)
    {
        if (!created)
        {
            return;
        }
        try
        {
            Directory.Delete(folder);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // The write error is the one worth reporting.
        }
    }
}
