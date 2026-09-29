using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class OrphansCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var command = new Command("orphans", "List pages with no link to or from another file; exits 1 when there are any")
        {
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var output = LinkQueries.Orphans(session.Db).Select(path => new OrphanOutput(path)).ToList();

            session.EmitList(output, OutputJson.Default.ListOrphanOutput, orphan => Format.Safe(orphan.Path));
            return output.Count == 0 ? ExitCode.Clean : ExitCode.Findings;
        }));
        return command;
    }
}
