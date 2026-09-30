using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class BackrefsCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var path = new Argument<string>("path")
        {
            Description = "A path in the workspace, relative to the working directory: a file, a folder, or the workspace root; it need not exist, so links broken on it show",
        };
        var kind = new Option<string>("--kind") { Description = "Only links of this kind", HelpName = "body|frontmatter" };
        kind.AcceptOnlyFromAmong("body", "frontmatter");
        var transitive = new Option<bool>("--transitive") { Description = "Every file that reaches the path through a chain of links" };
        var command = new Command("backrefs", "List the links into a path") { path, kind, transitive, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var relative = session.KeyOf(result.GetValue(path)!, allowRoot: true);

            if (result.GetValue(transitive))
            {
                var sources = LinkQueries.TransitiveBackrefs(session.Db, relative, result.GetValue(kind))
                    .Select(source => new TransitiveBackrefOutput(source)).ToList();
                session.EmitList(sources, OutputJson.Default.ListTransitiveBackrefOutput, s => Format.Safe(s.Source));
                return ExitCode.Clean;
            }

            var links = LinkQueries.Backrefs(session.Db, relative, result.GetValue(kind))
                .Select(l => new BackrefOutput(l.Source, l.Line, l.Kind, l.Raw)).ToList();
            session.EmitList(links, OutputJson.Default.ListBackrefOutput, link =>
                $"{Format.Safe(link.Source)}:{link.Line}  {link.Kind,-11}  {Format.Safe(link.Raw)}");
            return ExitCode.Clean;
        }));
        return command;
    }
}
