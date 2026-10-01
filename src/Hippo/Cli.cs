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

    public static RootCommand Build() => Build(CliEnvironment.Process());

    internal static RootCommand Build(CliEnvironment environment)
    {
        var root = new RootCommand("Indexes a markdown workspace and answers structural questions about it.");
        root.SetAction(result => new HelpAction().Invoke(result));
        root.Subcommands.Add(InitCommand.Build(environment));
        root.Subcommands.Add(IndexCommand.Build(environment));
        root.Subcommands.Add(StatusCommand.Build(environment));
        root.Subcommands.Add(FilesCommand.Build(environment));
        root.Subcommands.Add(ShowCommand.Build(environment));
        root.Subcommands.Add(RefsCommand.Build(environment));
        root.Subcommands.Add(BackrefsCommand.Build(environment));
        root.Subcommands.Add(BrokenCommand.Build(environment));
        root.Subcommands.Add(OrphansCommand.Build(environment));
        root.Subcommands.Add(LintCommand.Build(environment));
        root.Subcommands.Add(SearchCommand.Build(environment));
        root.Subcommands.Add(CacheCommand.Build(environment));
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>, returning an <see cref="ExitCode"/>; a usage error is an
    /// <see cref="ExitCode.Error"/>.</summary>
    public static int Run(string[] args, InvocationConfiguration? configuration = null) =>
        Run(args, CliEnvironment.Process(), configuration);

    /// <summary>Runs <paramref name="args"/> as <see cref="Run(string[], InvocationConfiguration?)"/> does, reading
    /// <paramref name="environment"/> in place of the process's.</summary>
    internal static int Run(string[] args, CliEnvironment environment, InvocationConfiguration? configuration = null)
    {
        var result = Build(environment).Parse(args);
        var exitCode = result.Invoke(configuration);
        return result.Action is ParseErrorAction ? ExitCode.Error : exitCode;
    }
}
