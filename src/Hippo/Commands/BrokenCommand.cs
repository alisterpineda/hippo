using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class BrokenCommand
{
    public static Command Build()
    {
        var command = new Command("broken", "List links whose target is not a file in the index; exits 1 when there are any") { WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var links = LinkQueries.Broken(session.Db);
            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                session.Output.WriteLine(JsonSerializer.Serialize(links, OutputJson.Default.ListBrokenLink));
            }
            else
            {
                foreach (var link in links)
                {
                    var target = link.Target switch
                    {
                        null => "outside the workspace",
                        "" => "the workspace root",
                        var path => Format.Safe(path),
                    };
                    session.Output.WriteLine($"{Format.Safe(link.Source)}:{link.Line}  {link.Kind,-11}  {Format.Safe(link.Raw)} -> {target}");
                }
            }
            return links.Count == 0 ? 0 : 1;
        }));
        return command;
    }
}
