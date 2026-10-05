using System.Reflection;

namespace Hippo.Tests.E2E;

/// <summary>
/// Keeps the command tests in step with the command tree: every leaf command the binary lists in its help must have a
/// class named from its words, <c>cache list</c> -> <c>CacheListCommandTests</c>, holding exactly one running (not
/// skipped) test with the <see cref="Traits.Smoke"/> trait, and every smoke test must sit in such a class.
/// </summary>
public class CommandCoverageTests
{
    [Fact]
    public async Task Every_leaf_command_has_a_class_with_one_smoke_test()
    {
        var classes = (await Leaves([])).ToDictionary(ClassName);
        var types = typeof(CommandCoverageTests).Assembly.GetTypes();
        var tests = types
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null && method.GetCustomAttributes<TraitAttribute>()
                .Any(trait => trait.Name == Traits.Category && trait.Value == Traits.Smoke))
            .ToList();

        var problems = new List<string>();
        foreach (var (name, leaf) in classes)
        {
            var type = types.SingleOrDefault(type => type.Name == name);
            var count = tests.Count(test => test.DeclaringType == type && test.GetCustomAttribute<FactAttribute>()!.Skip is null);
            if (type is null)
            {
                problems.Add($"'{leaf}' has no class {name}");
            }
            else if (count != 1)
            {
                problems.Add($"{name} has {count} running smoke tests, not 1");
            }
        }
        foreach (var test in tests.Where(test => !classes.ContainsKey(test.DeclaringType!.Name)))
        {
            problems.Add($"{test.DeclaringType!.Name}.{test.Name} is a smoke test outside a leaf command's class");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>The test class for <paramref name="leaf"/>: its words in PascalCase, then <c>CommandTests</c>.</summary>
    private static string ClassName(string leaf) =>
        string.Concat(leaf.Split(' ').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) + "CommandTests";

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
