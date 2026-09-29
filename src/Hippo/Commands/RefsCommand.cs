using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class RefsCommand
{
    public static Command Build()
    {
        var path = new Argument<string>("path") { Description = "A file in the workspace, relative to the working directory" };
        var command = new Command("refs", "List the links out of a file, and whether each target exists") { path, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var relative = session.Workspace.KeyOf(result.GetValue(path)!);
            _ = FileQueries.Get(session.Db, relative) ?? throw new HippoException($"{relative} is not in the index");
            var links = LinkQueries.Refs(session.Db, relative);

            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                session.Output.WriteLine(JsonSerializer.Serialize(links, OutputJson.Default.ListLinkOut));
            }
            else
            {
                foreach (var link in links)
                {
                    session.Output.WriteLine($"{link.Line,5}  {link.Kind,-11}  {link.Type,-7}  {Format.Safe(string.IsNullOrEmpty(link.Target) ? link.Raw : link.Target)}");
                }
            }
            return 0;
        }));
        return command;
    }
}
