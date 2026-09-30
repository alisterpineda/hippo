using System.CommandLine;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class FilesCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var glob = new Option<string>("--glob") { Description = "Only paths matching this workspace-relative glob", HelpName = "pattern" };
        var where = new Option<string>("--where") { Description = "Only files whose frontmatter field equals value", HelpName = "field=value" };
        var errors = new Option<bool>("--errors") { Description = "Only files whose frontmatter failed to parse, with the error" };
        var command = new Command("files", "List indexed files") { glob, where, errors, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var filter = result.GetValue(where) is { } text ? FrontmatterFilter.Parse(text) : null;
            var files = FileQueries.List(session.Db, filter);
            if (result.GetValue(glob) is { } pattern)
            {
                var matched = session.Workspace.Glob([pattern], files.Select(f => f.Path));
                files = files.Where(f => matched.Contains(f.Path)).ToList();
            }
            var errorsOnly = result.GetValue(errors);
            if (errorsOnly)
            {
                files = files.Where(f => f.ParseError is not null).ToList();
            }

            var output = files.Select(f => new FileOutput(f.Path, f.Kind, f.Size, Format.Modified(f.Mtime), f.ParseError)).ToList();
            session.EmitList(output, OutputJson.Default.ListFileOutput, file =>
                errorsOnly ? $"{Format.Safe(file.Path)}: {Format.Safe(file.ParseError!)}" : Format.Safe(file.Path));
            return ExitCode.Clean;
        }));
        return command;
    }
}
