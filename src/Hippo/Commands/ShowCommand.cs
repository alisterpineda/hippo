using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;

namespace Hippo.Commands;

internal static class ShowCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var path = new Argument<string>("path") { Description = "A file in the workspace, relative to the working directory" };
        var command = new Command("show", "Show what the index holds for one file") { path, WorkspaceSession.JsonOption };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var relative = session.KeyOf(result.GetValue(path)!);
            var file = FileQueries.Get(session.Db, relative) ?? throw new HippoException($"{relative} is not in the index");
            JsonElement? frontmatter = file.Frontmatter is null ? null : JsonDocument.Parse(file.Frontmatter).RootElement.Clone();
            var output = new ShowOutput(file.Path, file.Kind, file.Size, Format.Modified(file.Mtime), file.Hash, frontmatter, file.ParseError);

            session.Emit(output, OutputJson.Default.ShowOutput, (text, o) =>
            {
                text.WriteLine($"Path:        {Format.Safe(o.Path)}");
                text.WriteLine($"Kind:        {o.Kind}");
                text.WriteLine($"Size:        {o.Size} bytes");
                text.WriteLine($"Modified:    {o.Modified:O}");
                text.WriteLine($"Hash:        {o.Hash}");
                if (o.ParseError is not null)
                {
                    text.WriteLine($"Parse error: {Format.Safe(o.ParseError)}");
                }
                else if (o.Frontmatter is { } json)
                {
                    text.WriteLine("Frontmatter:");
                    text.WriteLine(Format.Indented(json));
                }
                else if (o.Kind == "markdown")
                {
                    text.WriteLine("Frontmatter: none");
                }
            });
            return ExitCode.Clean;
        }));
        return command;
    }
}
