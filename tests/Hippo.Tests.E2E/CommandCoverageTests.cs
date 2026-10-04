using System.Reflection;

namespace Hippo.Tests.E2E;

/// <summary>
/// Keeps the smoke tests in step with the command tree: every leaf command the binary lists in its help must be claimed
/// by a running (not skipped) test marked <see cref="CoversAttribute"/>, and every claim must name a command the
/// binary still has.
/// </summary>
public class CommandCoverageTests
{
    [Fact]
    public async Task Every_leaf_command_has_a_test_that_runs_it()
    {
        var leaves = await Leaves([]);
        var claimed = typeof(CoversAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttribute<FactAttribute>() is { Skip: null })
            .SelectMany(method => method.GetCustomAttributes<CoversAttribute>())
            .Select(covers => string.Join(' ', covers.Command))
            .Distinct();

        Assert.Equal(leaves.Order(StringComparer.Ordinal), claimed.Order(StringComparer.Ordinal));
    }

    /// <summary>The leaf commands under <paramref name="path"/>, each as its words joined by a space.</summary>
    private static async Task<List<string>> Leaves(string[] path)
    {
        var result = await HippoProcess.RunAsync([.. path, "--help"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        // An unknown word before --help still prints the parent's help, which would list the same children forever.
        Assert.True(UsagePath(result.Stdout).SequenceEqual(path), $"help for '{string.Join(' ', path)}' was another command's");

        var children = Commands(result.Stdout);
        if (children.Count == 0)
        {
            return [string.Join(' ', path)];
        }
        var leaves = new List<string>();
        foreach (var child in children)
        {
            leaves.AddRange(await Leaves([.. path, child]));
        }
        return leaves;
    }

    /// <summary>The command names in the <c>Commands:</c> section of System.CommandLine's help, or none when the
    /// section is absent. Each line there is the name, then any arguments (<c>find &lt;query&gt;</c>), then the
    /// description.</summary>
    private static List<string> Commands(string help)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        return lines.SkipWhile(line => line != "Commands:").Skip(1)
            .TakeWhile(line => line.Trim().Length > 0)
            .Select(line => line.Trim().Split(' ', ',')[0])
            .ToList();
    }

    /// <summary>The command words in the help's <c>Usage:</c> line, after the root name and before the first
    /// <c>[</c> or <c>&lt;</c> placeholder.</summary>
    private static IEnumerable<string> UsagePath(string help)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var usage = lines.SkipWhile(line => line != "Usage:").Skip(1).FirstOrDefault() ?? "";
        return usage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .TakeWhile(word => !word.StartsWith('[') && !word.StartsWith('<'));
    }
}
