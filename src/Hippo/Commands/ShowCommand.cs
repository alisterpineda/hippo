using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class ShowCommand
{
    public static Command Build()
    {
        var path = new Argument<string>("path") { Description = "A file in the workspace, relative to the working directory" };
        var command = new Command("show", "Show what the index holds for one file") { path, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, rebuild: false, session =>
        {
            var relative = session.Workspace.KeyOf(result.GetValue(path)!);
            var file = FileQueries.Get(session.Db, relative) ?? throw new HippoException($"{relative} is not in the index");
            JsonElement? frontmatter = file.Frontmatter is null ? null : JsonDocument.Parse(file.Frontmatter).RootElement.Clone();

            if (result.GetValue(WorkspaceSession.JsonOption))
            {
                var output = new ShowOutput(file.Path, file.Kind, file.Size, Format.Modified(file.Mtime), file.Hash, frontmatter, file.ParseError);
                session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.ShowOutput));
            }
            else
            {
                var output = session.Output;
                output.WriteLine($"Path:        {Format.Safe(file.Path)}");
                output.WriteLine($"Kind:        {file.Kind}");
                output.WriteLine($"Size:        {file.Size} bytes");
                output.WriteLine($"Modified:    {Format.Modified(file.Mtime):O}");
                output.WriteLine($"Hash:        {file.Hash}");
                if (file.ParseError is not null)
                {
                    output.WriteLine($"Parse error: {Format.Safe(file.ParseError)}");
                }
                else if (frontmatter is { } json)
                {
                    output.WriteLine("Frontmatter:");
                    output.WriteLine(Format.Indented(json));
                }
                else if (file.Kind == "markdown")
                {
                    output.WriteLine("Frontmatter: none");
                }
            }
            return 0;
        }));
        return command;
    }
}
