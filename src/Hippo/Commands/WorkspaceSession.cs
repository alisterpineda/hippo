using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Commands;

/// <summary>
/// What every workspace command starts from: the workspace found from the working directory, its migrated index, and
/// a sweep that has just brought the index in line with the files on disk.
/// </summary>
internal sealed record WorkspaceSession(
    Workspace Workspace, string WorkingDirectory, string DatabasePath, SqliteConnection Db, SweepResult Sweep, TextWriter Output,
    bool Json)
{
    public static readonly Option<bool> JsonOption = new("--json") { Description = "Print JSON instead of text" };

    /// <summary>Opens the session and runs <paramref name="command"/> under <see cref="Guard"/>.</summary>
    public static int Run(ParseResult result, CliEnvironment environment, bool rebuild, Func<WorkspaceSession, int> command)
    {
        var error = result.InvocationConfiguration.Error;
        return Guard.Run(error, () =>
        {
            var workspace = Workspace.Open(environment.WorkingDirectory);
            var databasePath = CacheLocation.DatabasePath(workspace.Root, environment.GetVariable);
            using var db = IndexDatabase.Open(databasePath, out var scriptsApplied);
            // A schema change may alter what the sweep stores for unchanged files, so it re-reads them all.
            var sweep = Sweeper.Run(workspace, db, rebuild || scriptsApplied > 0, environment.Clock);
            Warn(error, sweep.Warnings);

            var session = new WorkspaceSession(workspace, environment.WorkingDirectory, databasePath, db, sweep,
                result.InvocationConfiguration.Output, result.GetValue(JsonOption));
            return command(session);
        });
    }

    /// <summary>The key of <paramref name="path"/>, a path the user gave relative to the working directory.</summary>
    public string KeyOf(string path) => Workspace.KeyOf(path, WorkingDirectory);

    /// <summary>Prints <paramref name="value"/> as JSON under <c>--json</c>, and otherwise as <paramref name="text"/>
    /// writes it. Both print the one value, so the two forms say the same.</summary>
    public void Emit<T>(T value, JsonTypeInfo<T> json, Action<TextWriter, T> text)
    {
        if (Json)
        {
            Output.WriteLine(JsonSerializer.Serialize(value, json));
        }
        else
        {
            text(Output, value);
        }
    }

    /// <summary>Prints <paramref name="items"/> as a JSON array under <c>--json</c>, and otherwise one
    /// <paramref name="line"/> per item.</summary>
    public void EmitList<T>(List<T> items, JsonTypeInfo<List<T>> json, Func<T, string> line) =>
        Emit(items, json, (output, list) =>
        {
            foreach (var item in list)
            {
                output.WriteLine(line(item));
            }
        });

    private static void Warn(TextWriter error, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            error.WriteLine($"hippo: warning: {Format.Safe(warning)}");
        }
    }
}
