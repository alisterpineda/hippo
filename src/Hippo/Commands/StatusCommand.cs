using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class StatusCommand
{
    public static Command Build()
    {
        var command = new Command("status", "Show the workspace root, database path, file counts and last sweep") { WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var counts = FileQueries.Count(session.Db);
            var sweep = session.Sweep;
            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                var output = new StatusOutput(
                    session.Workspace.Root,
                    session.DatabasePath,
                    new CountsOutput(counts.Total, counts.Markdown, counts.Plain, counts.ParseErrors),
                    new SweepOutput(sweep.FinishedAt, Format.Milliseconds(sweep.Elapsed), sweep.Added, sweep.Updated, sweep.Removed));
                session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.StatusOutput));
            }
            else
            {
                var output = session.Output;
                output.WriteLine($"Workspace:   {session.Workspace.Root}");
                output.WriteLine($"Database:    {session.DatabasePath}");
                output.WriteLine($"Files:       {counts.Total} ({counts.Markdown} markdown, {counts.Plain} plain)");
                output.WriteLine($"Frontmatter: {counts.ParseErrors} with errors");
                output.WriteLine($"Last sweep:  {sweep.FinishedAt:O}, {Format.Milliseconds(sweep.Elapsed)} ms: {Format.Summary(sweep)}");
            }
            return 0;
        }));
        return command;
    }
}
