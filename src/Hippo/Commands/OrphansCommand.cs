using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Commands;

internal static class OrphansCommand
{
    public static Command Build()
    {
        var command = new Command("orphans", "List pages no other file links to, except the roots in .hippo.yaml; exits 1 when there are any")
        {
            NotebookSession.JsonOption,
        };
        command.SetAction(result => NotebookSession.Run(result, rebuild: false, session =>
        {
            var notebook = session.Notebook;
            var orphans = LinkQueries.Orphans(session.Db);
            if (notebook.Config.Links.Roots.Count > 0)
            {
                var roots = new Matcher(StringComparison.Ordinal);
                roots.AddIncludePatterns(notebook.Config.Links.Roots);
                var exempt = notebook.Match(roots, orphans.Select(notebook.FullPath));
                orphans = orphans.Where(path => !exempt.Contains(path)).ToList();
            }

            if (result.GetValue(NotebookSession.JsonOption))
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
