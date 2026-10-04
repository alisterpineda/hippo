using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Reflection;
using Hippo.Commands;
using Hippo.Commands.Find;

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
        root.Subcommands.Add(FindCommand.Build(environment));
        root.Subcommands.Add(ShowCommand.Build(environment));
        root.Subcommands.Add(RefsCommand.Build(environment));
        root.Subcommands.Add(BackrefsCommand.Build(environment));
        var lint = LintCommand.Build(environment);
        root.Subcommands.Add(lint);
        root.Subcommands.Add(CacheCommand.Build(environment));
        var help = root.Options.OfType<HelpOption>().Single();
        help.Action = new HelpWithRules((HelpAction)help.Action!, lint);
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>, returning an <see cref="ExitCode"/>. A usage error prints the
    /// error and where to find the command's help, not the help itself, and is an <see cref="ExitCode.Error"/>.</summary>
    public static int Run(string[] args, InvocationConfiguration? configuration = null) =>
        Run(args, CliEnvironment.Process(), configuration);

    /// <summary>Runs <paramref name="args"/> as <see cref="Run(string[], InvocationConfiguration?)"/> does, reading
    /// <paramref name="environment"/> in place of the process's.</summary>
    internal static int Run(string[] args, CliEnvironment environment, InvocationConfiguration? configuration = null)
    {
        var result = Build(environment).Parse(args);
        if (result.Action is not ParseErrorAction usageError)
        {
            return result.Invoke(configuration);
        }
        usageError.ShowHelp = false;
        result.Invoke(configuration);
        result.InvocationConfiguration.Error.WriteLine($"Run '{CommandLine(result.CommandResult)} --help' for usage.");
        return ExitCode.Error;
    }

    /// <summary>The help, and for <c>lint</c>, its rules after it: the help's layout is not open to change.</summary>
    private sealed class HelpWithRules(HelpAction help, Command lint) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => help.ClearsParseErrors;

        public override int Invoke(ParseResult result)
        {
            var exitCode = help.Invoke(result);
            if (result.CommandResult.Command == lint)
            {
                LintCommand.WriteRules(result.InvocationConfiguration.Output);
            }
            return exitCode;
        }
    }

    /// <summary>How the command <paramref name="command"/> parsed to is typed: <c>hippo</c>, then each subcommand.</summary>
    private static string CommandLine(CommandResult command) =>
        command.Parent is CommandResult parent ? $"{CommandLine(parent)} {command.Command.Name}" : "hippo";
}
