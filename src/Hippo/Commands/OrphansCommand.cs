using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Commands;

internal static class OrphansCommand
{
    public static Command Build()
    {
        var command = new Command("orphans", $"List pages no other file links to, except the roots in {WorkspaceConfig.RelativePath}; exits 1 when there are any")
        {
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var workspace = session.Workspace;
            var orphans = LinkQueries.Orphans(session.Db);
            if (workspace.Config.Links.Roots.Count > 0)
            {
                var roots = new Matcher(StringComparison.Ordinal);
                roots.AddIncludePatterns(workspace.Config.Links.Roots);
                var exempt = workspace.Match(roots, orphans.Select(workspace.FullPath));
                orphans = orphans.Where(path => !exempt.Contains(path)).ToList();
            }

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
