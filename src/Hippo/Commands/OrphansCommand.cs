using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class OrphansCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var exclude = new Option<string[]>("--exclude")
        {
            Description = "Leave out paths matching this workspace-relative glob; repeatable",
            HelpName = "pattern",
        };
        var command = new Command("orphans", "List files with no link to or from another file; exits 1 when there are any")
        {
            exclude,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var paths = LinkQueries.Orphans(session.Db);
            // This only hides rows: an excluded file's links still keep what they reach from being an orphan.
            if (result.GetValue(exclude) is { Length: > 0 } patterns)
            {
                var excluded = session.Workspace.Glob(patterns, paths);
                paths = paths.Where(path => !excluded.Contains(path)).ToList();
            }
            var output = paths.Select(path => new OrphanOutput(path)).ToList();

            session.EmitList(output, OutputJson.Default.ListOrphanOutput, orphan => Format.Safe(orphan.Path));
            return output.Count == 0 ? ExitCode.Clean : ExitCode.Findings;
        }));
        return command;
    }
}
