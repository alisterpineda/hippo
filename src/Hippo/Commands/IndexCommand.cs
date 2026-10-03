using System.CommandLine;

namespace Hippo.Commands;

internal static class IndexCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var rebuild = new Option<bool>("--rebuild") { Description = "Re-read and re-parse every file, replacing the whole index" };
        var command = new Command("index",
            "Bring the index up to date with the workspace; every workspace command does this first, so index is only needed "
            + "for --rebuild") { rebuild, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, result.GetValue(rebuild), session =>
        {
            var sweep = session.Sweep;
            var output = new IndexOutput(sweep.Files, sweep.Added, sweep.Updated, sweep.Removed, sweep.Hashed, sweep.Rebuilt,
                Format.Milliseconds(sweep.Elapsed));
            // A rebuild re-reads every file, so a count of what changed since the last sweep would read as no work done.
            // The sweep says whether it was one: a migration rebuilds the index without --rebuild, and a first sweep
            // has nothing to re-read, so its count of added files says what it did.
            session.Emit(output, OutputJson.Default.IndexOutput, (text, o) => text.WriteLine(sweep.RereadEveryFile
                ? $"Rebuilt the index from {o.Files} files in {o.ElapsedMs} ms."
                : $"Indexed {o.Files} files in {o.ElapsedMs} ms: {Format.Summary(o.Added, o.Updated, o.Removed)}."));
            return ExitCode.Clean;
        }));
        return command;
    }
}
