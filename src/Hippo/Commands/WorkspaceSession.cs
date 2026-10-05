using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hippo.Cache;
using Hippo.Indexing;
using Hippo.Workspaces;
using Microsoft.Data.Sqlite;

namespace Hippo.Commands;

/// <summary>
/// What every workspace command starts from: the workspace found from the working directory, its migrated index, and
/// a sweep that has just brought the index in line with the files on disk. <see cref="Styled"/> says whether text
/// output may be styled, as <see cref="CliEnvironment.Styled"/> does.
/// </summary>
internal sealed record WorkspaceSession(
    Workspace Workspace, string WorkingDirectory, string DatabasePath, SqliteConnection Db, SweepResult Sweep, TextWriter Output,
    TextWriter Error, bool Json, bool Styled)
{
    public static readonly Option<bool> JsonOption = new("--json") { Description = "Print JSON instead of text" };

    /// <summary>The <c>--link-kind</c> option, which accepts the link kinds the index records.</summary>
    public static Option<string> LinkKindOption(string description)
    {
        var option = new Option<string>("--link-kind") { Description = description, HelpName = "body|frontmatter" };
        option.AcceptOnlyFromAmong("body", "frontmatter");
        return option;
    }

    /// <summary>Opens the session and runs <paramref name="command"/> under <see cref="Guard"/>.</summary>
    public static int Run(ParseResult result, CliEnvironment environment, bool rebuild, Func<WorkspaceSession, int> command)
    {
        var error = result.InvocationConfiguration.Error;
        return Guard.Run(error, () =>
        {
            var workspace = Workspace.Open(environment.WorkingDirectory);
            var canonicalRoot = CanonicalPath.Of(workspace.Root);
            var databasePath = CacheLocation.DatabasePath(canonicalRoot, environment.GetVariable);
            using var db = IndexDatabase.Open(databasePath);
            IndexMeta.RecordRoot(db, canonicalRoot, root => MountPoints.Containing(root, environment.GetMountPoints()));
            var sweep = Sweeper.Run(workspace, db, rebuild, environment.Clock);
            Warn(error, sweep.Warnings);

            var session = new WorkspaceSession(workspace, environment.WorkingDirectory, databasePath, db, sweep,
                result.InvocationConfiguration.Output, error, result.GetValue(JsonOption), environment.Styled);
            return command(session);
        });
    }

    /// <summary>The key of <paramref name="path"/>, a path the user gave relative to the working directory.</summary>
    public string KeyOf(string path, bool allowRoot = false) => Workspace.KeyOf(path, WorkingDirectory, allowRoot);

    /// <summary>Prints <paramref name="value"/> as JSON under <c>--json</c>, and otherwise as <paramref name="text"/>
    /// writes it. Both print the one value, so the two forms say the same.</summary>
    public void Emit<T>(T value, JsonTypeInfo<T> json, Action<TextWriter, T> text) => Emit(Output, Json, value, json, text);

    /// <summary>Prints <paramref name="items"/> as a JSON array under <c>--json</c>, and otherwise one
    /// <paramref name="line"/> per item.</summary>
    public void EmitList<T>(List<T> items, JsonTypeInfo<List<T>> json, Func<T, string> line) =>
        EmitList(Output, Json, items, json, line);

    /// <summary><see cref="Emit{T}(T, JsonTypeInfo{T}, Action{TextWriter, T})"/> for a command that opens no
    /// session.</summary>
    public static void Emit<T>(TextWriter output, bool asJson, T value, JsonTypeInfo<T> json, Action<TextWriter, T> text)
    {
        if (asJson)
        {
            output.WriteLine(JsonSerializer.Serialize(value, json));
        }
        else
        {
            text(output, value);
        }
    }

    /// <summary><see cref="EmitList{T}(List{T}, JsonTypeInfo{List{T}}, Func{T, string})"/> for a command that opens no
    /// session.</summary>
    public static void EmitList<T>(TextWriter output, bool asJson, List<T> items, JsonTypeInfo<List<T>> json, Func<T, string> line) =>
        Emit(output, asJson, items, json, (writer, list) =>
        {
            foreach (var item in list)
            {
                writer.WriteLine(line(item));
            }
        });

    /// <summary>Prints <paramref name="warning"/> to the error stream.</summary>
    public void Warn(string warning) => Warn(Error, [warning]);

    public static void Warn(TextWriter error, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            error.WriteLine($"hippo: warning: {Format.Safe(warning)}");
        }
    }
}
