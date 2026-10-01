using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class StatusCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var command = new Command("status", "Show the workspace root, database path, file counts and last sweep") { WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var counts = FileQueries.Count(session.Db);
            var sweep = session.Sweep;
            var output = new StatusOutput(
                session.Workspace.Root,
                session.DatabasePath,
                new CountsOutput(counts.Total, counts.Markdown, counts.Other, counts.ParseErrors),
                new SweepOutput(sweep.FinishedAt, Format.Milliseconds(sweep.Elapsed), sweep.Added, sweep.Updated, sweep.Removed));
            session.Emit(output, OutputJson.Default.StatusOutput, (text, o) =>
            {
                var last = o.LastSweep;
                text.WriteLine($"Workspace:   {Format.Safe(o.Root)}");
                text.WriteLine($"Database:    {Format.Safe(o.Database)}");
                text.WriteLine($"Files:       {o.Files.Total} ({o.Files.Markdown} markdown, {o.Files.Other} other)");
                var hint = o.Files.ParseErrors > 0 ? " (hippo find --errors)" : "";
                text.WriteLine($"Frontmatter: {o.Files.ParseErrors} with errors{hint}");
                text.WriteLine($"Last sweep:  {last.FinishedAt:O}, {last.ElapsedMs} ms: {Format.Summary(last.Added, last.Updated, last.Removed)}");
            });
            return ExitCode.Clean;
        }));
        return command;
    }
}
