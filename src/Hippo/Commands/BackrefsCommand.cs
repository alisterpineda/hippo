using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class BackrefsCommand
{
    public static Command Build()
    {
        var path = new Argument<string>("path")
        {
            Description = "A path in the notebook, relative to the working directory; it need not exist, so links broken on it show",
        };
        var kind = new Option<string>("--kind") { Description = "Only links of this kind", HelpName = "body|frontmatter" };
        kind.AcceptOnlyFromAmong("body", "frontmatter");
        var transitive = new Option<bool>("--transitive") { Description = "Every file that reaches the path through a chain of links" };
        var command = new Command("backrefs", "List the links into a path") { path, kind, transitive, NotebookSession.JsonOption };
        command.SetAction(result => NotebookSession.Run(result, rebuild: false, session =>
        {
            var relative = session.Notebook.KeyOf(result.GetValue(path)!);
            var json = result.GetValue(NotebookSession.JsonOption);

            if (result.GetValue(transitive))
            {
                var sources = LinkQueries.TransitiveBackrefs(session.Db, relative, result.GetValue(kind));
                if (json)
                {
                    var output = sources.Select(source => new TransitiveBackrefOutput(source)).ToList();
                    session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.ListTransitiveBackrefOutput));
                }
                else
                {
                    foreach (var source in sources)
                    {
                        session.Output.WriteLine(Format.Safe(source));
                    }
                }
                return 0;
            }

            var links = LinkQueries.Backrefs(session.Db, relative, result.GetValue(kind));
            if (json)
            {
                session.Output.WriteLine(JsonSerializer.Serialize(links, OutputJson.Default.ListLinkIn));
            }
            else
            {
                foreach (var link in links)
                {
                    session.Output.WriteLine($"{Format.Safe(link.Source)}:{link.Line}  {link.Kind,-11}  {Format.Safe(link.Raw)}");
                }
            }
            return 0;
        }));
        return command;
    }
}
