using System.CommandLine;
using System.Text.Json;

namespace Hippo.Commands;

internal static class IndexCommand
{
    public static Command Build()
    {
        var rebuild = new Option<bool>("--rebuild") { Description = "Re-read and re-parse every file, replacing the whole index" };
        var command = new Command("index", "Bring the index up to date with the notebook") { rebuild, NotebookSession.JsonOption };
        command.SetAction(result => NotebookSession.Run(result, result.GetValue(rebuild), session =>
        {
            var sweep = session.Sweep;
            if (result.GetValue(NotebookSession.JsonOption))
            {
                var output = new IndexOutput(sweep.Files, sweep.Added, sweep.Updated, sweep.Removed, sweep.Hashed, sweep.Rebuilt,
                    Format.Milliseconds(sweep.Elapsed));
                session.Output.WriteLine(JsonSerializer.Serialize(output, OutputJson.Default.IndexOutput));
            }
            else
            {
                session.Output.WriteLine($"Indexed {sweep.Files} files in {Format.Milliseconds(sweep.Elapsed)} ms: {Format.Summary(sweep)}.");
            }
            return 0;
        }));
        return command;
    }
}
