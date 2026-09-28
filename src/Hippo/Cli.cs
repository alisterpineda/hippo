using System.CommandLine;
using System.Reflection;

namespace Hippo;

public static class Cli
{
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static RootCommand Build()
    {
        var root = new RootCommand("Indexes a markdown notebook and answers structural questions about it.");
        root.SetAction(result =>
        {
            result.InvocationConfiguration.Output.WriteLine($"hippo {Version}: nothing indexed yet");
            return 0;
        });
        return root;
    }
}
