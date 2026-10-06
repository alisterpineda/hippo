using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class BackrefsCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var path = new Argument<string>("path")
        {
            Description = "A path in the workspace, relative to the working directory: a file, a folder, or the workspace root; it need not exist, so links broken on it show; "
                + "a folder's backrefs are the links to the folder itself, not to the files in it",
        };
        var linkKind = WorkspaceSession.LinkKindOption("Only links of this kind");
        var from = new Option<string[]>("--from")
        {
            Description = "Only links from files matching this workspace-relative glob, or with a leading !, not matching it; repeatable",
            HelpName = "pattern",
        };
        var transitive = new Option<bool>("--transitive")
        {
            Description = "Every file that reaches the path through a chain of links; --from and --link-kind apply at every hop",
        };
        var command = new Command("backrefs", "List the links into a path") { path, linkKind, from, transitive, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var relative = session.KeyOf(result.GetValue(path)!, allowRoot: true);
            var kind = result.GetValue(linkKind);
            var sourceFiles = result.GetValue(from) is { Length: > 0 } patterns
                ? session.Workspace.Glob(patterns, LinkQueries.Sources(session.Db, null))
                : null;

            if (result.GetValue(transitive))
            {
                var sources = LinkQueries.TransitiveBackrefs(session.Db, relative, kind, sourceFiles)
                    .Select(source => new TransitiveBackrefOutput(source)).ToList();
                session.EmitList(sources, OutputJson.Default.ListTransitiveBackrefOutput, s => Format.Safe(s.Source));
                return ExitCode.Clean;
            }

            var links = LinkQueries.Backrefs(session.Db, relative, kind, sourceFiles)
                .Select(l => new BackrefOutput(l.Source, l.Line, l.Kind, l.Raw, l.Text)).ToList();
            var width = links.Count == 0 ? 0 : links.Max(link => Location(link).Length);
            session.EmitList(links, OutputJson.Default.ListBackrefOutput, link =>
                $"{Location(link).PadRight(width)}  {link.Kind,-11}  {Format.Safe(link.Raw)}{Format.LinkText(link.Text)}");
            return ExitCode.Clean;
        }));
        return command;
    }

    private static string Location(BackrefOutput link) => $"{Format.Safe(link.Source)}:{link.Line}";
}
