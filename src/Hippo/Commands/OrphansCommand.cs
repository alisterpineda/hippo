using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class OrphansCommand
{
    public static Command Build()
    {
        var command = new Command("orphans", "List pages with no link to or from another file; exits 1 when there are any")
        {
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var orphans = LinkQueries.Orphans(session.Db);

            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                var output = orphans.Select(path => new OrphanOutput(path)).ToList();
                session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.ListOrphanOutput));
            }
            else
            {
                foreach (var path in orphans)
                {
                    session.Output.WriteLine(Format.Safe(path));
                }
            }
            return orphans.Count == 0 ? 0 : 1;
        }));
        return command;
    }
}
