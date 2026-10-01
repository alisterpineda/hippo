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
            Description = "Only this rule, even one lint.off turns off; repeatable",
            HelpName = "name",
        };
        rule.AcceptOnlyFromAmong(LintRules.Names);
        var command = new Command("lint", "List broken links, and where OKF bundles depart from OKF v0.2; exits 1 when there are any findings")
        {
            rule,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var rules = result.GetValue(rule) is { Length: > 0 } named
                ? named.ToHashSet(StringComparer.Ordinal)
                : LintRules.Names.Except(session.Workspace.Config.LintOff).ToHashSet(StringComparer.Ordinal);

            var bundles = session.Sweep.OkfBundles;
            if (LintRules.All.Any(r => r.IsOkf && rules.Contains(r.Name)))
            {
                if (bundles.Count == 0)
                {
                    session.Warn("no bundle in links.bundles declares okf_version in its root index.md, so there is no OKF bundle to check");
                }
                foreach (var bundle in bundles.Where(b => b.Version != OkfBundle.SpecVersion))
                {
                    var declared = bundle.Version is null ? "an okf_version that is not a version" : $"okf_version {bundle.Version}";
                    session.Warn($"{bundle.Root} declares {declared}; hippo reads it as OKF {OkfBundle.SpecVersion}");
                }
            }

            var stored = FindingQueries.List(session.Db)
                .Where(f => rules.Contains(f.Rule))
                .Select(f => new FindingOutput(f.Rule, f.Path, f.Line, f.Message, JsonSerializer.Deserialize(f.Related, OutputJson.Default.ListString)!));
            var worked = new List<FindingOutput>();
            if (rules.Contains(OkfRules.Index))
            {
                worked.AddRange(IndexSync.Check(session.Db, bundles.Select(b => b.Root).ToList(), session.Workspace.Config.Links.Bundles)
                    .Select(f => new FindingOutput(OkfRules.Index, f.Path, f.Line, f.Message, f.Related)));
            }
            if (rules.Contains(LintRules.BrokenLink))
            {
                worked.AddRange(LinkQueries.Broken(session.Db).Select(l => l.Target is null
                    ? new FindingOutput(LintRules.BrokenLink, l.Source, l.Line, $"{l.Raw} -> outside the workspace", [])
                    : new FindingOutput(LintRules.BrokenLink, l.Source, l.Line, $"{l.Raw} -> {l.Target}", [l.Target])));
            }
            var output = FindingQueries.Merge(stored, worked, f => f.Path, f => f.Line);

            session.EmitList(output, OutputJson.Default.ListFindingOutput, finding =>
            {
                var location = finding.Line is { } line ? $"{Format.Safe(finding.Path)}:{line}" : Format.Safe(finding.Path);
                return $"{location}  {finding.Rule}  {Format.Safe(finding.Message)}";
            });
            return output.Count == 0 ? ExitCode.Clean : ExitCode.Findings;
        }));
        return command;
    }
}
