using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class RefsCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var path = new Argument<string>("path") { Description = "A file in the workspace, relative to the working directory" };
        var command = new Command("refs", "List the links out of a file, and whether each target exists") { path, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var relative = session.KeyOf(result.GetValue(path)!);
            var file = FileQueries.Get(session.Db, relative) ?? throw new HippoException($"{relative} is not in the index");
            var output = LinkQueries.Refs(session.Db, file.Path)
                .Select(l => new RefOutput(l.Line, l.Kind, l.Type, l.Raw, l.Target, l.Text)).ToList();

            session.EmitList(output, OutputJson.Default.ListRefOutput, link =>
                $"{link.Line,5}  {link.Kind,-11}  {link.Type,-9}  {Format.Safe(string.IsNullOrEmpty(link.Target) ? link.Raw : link.Target)}{Format.LinkText(link.Text)}");
            return ExitCode.Clean;
        }));
        return command;
    }
}
