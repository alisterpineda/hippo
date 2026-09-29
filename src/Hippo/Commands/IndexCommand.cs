using System.CommandLine;

namespace Hippo.Commands;

internal static class IndexCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var rebuild = new Option<bool>("--rebuild") { Description = "Re-read and re-parse every file, replacing the whole index" };
        var command = new Command("index", "Bring the index up to date with the workspace") { rebuild, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, result.GetValue(rebuild), session =>
        {
            var sweep = session.Sweep;
            var output = new IndexOutput(sweep.Files, sweep.Added, sweep.Updated, sweep.Removed, sweep.Hashed, sweep.Rebuilt,
                Format.Milliseconds(sweep.Elapsed));
            session.Emit(output, OutputJson.Default.IndexOutput, (text, o) =>
                text.WriteLine($"Indexed {o.Files} files in {o.ElapsedMs} ms: {Format.Summary(o.Added, o.Updated, o.Removed)}."));
            return ExitCode.Clean;
        }));
        return command;
    }
}
