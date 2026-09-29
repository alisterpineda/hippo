using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class BrokenCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var command = new Command("broken", "List links whose target is not a file in the index; exits 1 when there are any") { WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var output = LinkQueries.Broken(session.Db).Select(l => new BrokenOutput(l.Source, l.Line, l.Kind, l.Raw, l.Target)).ToList();

            session.EmitList(output, OutputJson.Default.ListBrokenOutput, link =>
            {
                var target = link.Target switch
                {
                    null => "outside the workspace",
                    "" => "the workspace root",
                    var path => Format.Safe(path),
                };
                return $"{Format.Safe(link.Source)}:{link.Line}  {link.Kind,-11}  {Format.Safe(link.Raw)} -> {target}";
            });
            return output.Count == 0 ? ExitCode.Clean : ExitCode.Findings;
        }));
        return command;
    }
}
