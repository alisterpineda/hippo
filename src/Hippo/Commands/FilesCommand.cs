using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Commands;

internal static class FilesCommand
{
    public static Command Build()
    {
        var glob = new Option<string>("--glob") { Description = "Only paths matching this workspace-relative glob", HelpName = "pattern" };
        var where = new Option<string>("--where") { Description = "Only files whose frontmatter field equals value", HelpName = "field=value" };
        var command = new Command("files", "List indexed files") { glob, where, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var filter = result.GetValue(where) is { } text ? FrontmatterFilter.Parse(text) : null;
            var files = FileQueries.List(session.Db, filter);
            if (result.GetValue(glob) is { } pattern)
            {
                files = Glob(session.Workspace, pattern, files);
            }

            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                var output = files.Select(f => new FileOutput(f.Path, f.Kind, f.Size, Format.Modified(f.Mtime))).ToList();
                session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.ListFileOutput));
            }
            else
            {
                foreach (var file in files)
                {
                    session.Output.WriteLine(Format.Safe(file.Path));
                }
            }
            return 0;
        }));
        return command;
    }

    private static List<FileListing> Glob(Workspace workspace, string pattern, List<FileListing> files)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(pattern);
        var matched = workspace.Match(matcher, files.Select(f => workspace.FullPath(f.Path)));
        return files.Where(f => matched.Contains(f.Path)).ToList();
    }
}
