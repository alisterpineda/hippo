using System.CommandLine;
using Hippo.Notebooks;

namespace Hippo.Commands;

/// <summary>Makes the working directory a notebook by writing a starter <c>.hippo.yaml</c>. Unlike the other commands
/// it needs no notebook, so it does not open a <see cref="NotebookSession"/>.</summary>
internal static class InitCommand
{
    public static Command Build()
    {
        var command = new Command("init", "Make the current folder a notebook by writing a starter .hippo.yaml");
        command.SetAction(result =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            try
            {
                var directory = Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory());
                var path = Path.Combine(directory, NotebookConfig.FileName);
                // Found before the config is written, so it is the enclosing notebook, if any, and never this folder.
                var enclosing = Notebook.FindRoot(directory);
                Write(path);

                if (enclosing is not null)
                {
                    error.WriteLine($"hippo: warning: {Format.Safe(directory)} is inside the notebook at {Format.Safe(enclosing)}, " +
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

    /// <summary>Writes the starter config to <paramref name="path"/>, never over an existing file, and removes what it
    /// created if the write fails partway so a rerun is not refused.</summary>
    private static void Write(string path)
    {
        FileStream stream;
        try
        {
            // CreateNew is the existence check: an existing config, even one written a moment ago, is never overwritten.
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new HippoException($"{path} already exists");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HippoException($"cannot write {path}: {ex.Message}");
        }

        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(NotebookConfig.Starter);
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
            throw new HippoException($"cannot write {path}: {ex.Message}");
        }
    }
}
