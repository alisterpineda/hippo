using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Reflection;
using Hippo.Commands;

namespace Hippo;

public static class Cli
{
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static RootCommand Build()
    {
        var root = new RootCommand("Indexes a markdown notebook and answers structural questions about it.");
        root.SetAction(result => new HelpAction().Invoke(result));
        root.Subcommands.Add(InitCommand.Build());
        root.Subcommands.Add(IndexCommand.Build());
        root.Subcommands.Add(StatusCommand.Build());
        root.Subcommands.Add(FilesCommand.Build());
        root.Subcommands.Add(ShowCommand.Build());
        root.Subcommands.Add(RefsCommand.Build());
        root.Subcommands.Add(BackrefsCommand.Build());
        root.Subcommands.Add(BrokenCommand.Build());
        root.Subcommands.Add(OrphansCommand.Build());
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>. Exits 0 when clean, 1 when a command reports findings, and 2
    /// on error, usage errors included.</summary>
    public static int Run(string[] args, InvocationConfiguration? configuration = null)
    {
        var result = Build().Parse(args);
        var exitCode = result.Invoke(configuration);
        return result.Action is ParseErrorAction ? 2 : exitCode;
    }
}
