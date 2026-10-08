using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Hippo.Okf;
using Hippo.Workspaces;

namespace Hippo.Commands;

internal static class LintCommand
{
    public static Command Build(CliEnvironment environment)
    {
        var rule = new Option<string[]>("--rule")
        {
            Description = "Only this rule, even one lint.off turns off; * matches any run of characters, as in okf-*; repeatable",
            HelpName = "name",
        };
        rule.Validators.Add(result =>
        {
            foreach (var pattern in result.GetValueOrDefault<string[]>() ?? [])
            {
                if (LintRules.Match(pattern).Count == 0)
                {
                    result.AddError($"--rule: {LintRules.NoMatch(pattern)}");
                }
            }
        });
        var command = new Command("lint", "List broken links, frontmatter that fails to parse, and where OKF bundles depart from OKF v0.2; exits 1 when there are any findings")
        {
            rule,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var named = result.GetValue(rule) is { Length: > 0 } patterns ? patterns : null;
            var rules = named is not null
                ? named.SelectMany(LintRules.Match).ToHashSet(StringComparer.Ordinal)
                : LintRules.Names.Except(session.Workspace.LintOffEverywhere()).ToHashSet(StringComparer.Ordinal);

            var bundles = session.Sweep.OkfBundles;
            // A workspace that lists no bundles never asked for OKF, so only one that lists some hears that none declares it.
            if (bundles.Count == 0 && session.Workspace.Config.Bundles.Count > 0 && LintRules.All.Any(r => r.IsOkf && rules.Contains(r.Name)))
            {
                session.Warn("no bundle in bundles declares okf_version in its root index.md, so there is no OKF bundle to check");
            }

            var stored = FindingQueries.List(session.Db)
                .Where(f => rules.Contains(f.Rule))
                .Select(f => new FindingOutput(f.Rule, f.Path, f.Line, f.Message, JsonSerializer.Deserialize(f.Related, OutputJson.Default.ListString)!));
            var worked = new List<FindingOutput>();
            if (rules.Contains(OkfRules.Index))
            {
                worked.AddRange(IndexSync.Check(session.Db, bundles.Select(b => b.Root).ToList(), session.Workspace.Config.Bundles)
                    .Select(f => new FindingOutput(OkfRules.Index, f.Path, f.Line, f.Message, f.Related)));
            }
            if (rules.Contains(LintRules.BrokenLink))
            {
                worked.AddRange(LinkQueries.Broken(session.Db).Select(l => l.Target is null
                    ? new FindingOutput(LintRules.BrokenLink, l.Source, l.Line, $"{l.Raw} -> outside the workspace", [])
                    : new FindingOutput(LintRules.BrokenLink, l.Source, l.Line, $"{l.Raw} -> {l.Target}", [l.Target])));
            }
            if (rules.Contains(LintRules.FrontmatterSyntax))
            {
                worked.AddRange(FileQueries.ParseErrors(session.Db)
                    .Select(f => new FindingOutput(LintRules.FrontmatterSyntax, f.Path, null, f.ParseError, [])));
            }
            // --rule brings back the rules it names, but not the files an entry without rules leaves out.
            var merged = FindingQueries.Merge(stored, worked, f => f.Path, f => f.Line);
            var off = session.Workspace.LintOff(merged.Select(f => f.Path).Distinct(StringComparer.Ordinal), named is not null);
            var output = merged.Where(f => !off(f.Rule, f.Path)).ToList();

            session.EmitList(new LintOutput(output), OutputJson.Default.LintOutput, o => o.Findings, finding =>
            {
                var location = finding.Line is { } line ? $"{Format.Safe(finding.Path)}:{line}" : Format.Safe(finding.Path);
                return $"{location}  {finding.Rule}  {Format.Safe(finding.Message)}";
            });
            return output.Count == 0 ? ExitCode.Clean : ExitCode.Findings;
        }));
        return command;
    }

    /// <summary>What <c>hippo lint --help</c> ends with: every rule and what it reports, laid out as the options
    /// are.</summary>
    public static void WriteRules(TextWriter output)
    {
        var width = LintRules.All.Max(r => r.Name.Length);
        output.WriteLine("Rules:");
        foreach (var rule in LintRules.All)
        {
            output.WriteLine($"  {rule.Name.PadRight(width)}  {rule.Description}");
        }
        output.WriteLine();
    }
}
