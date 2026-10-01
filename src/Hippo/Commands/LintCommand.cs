using System.CommandLine;
using System.Text.Json;
using Hippo.Indexing;
using Hippo.Okf;

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
        rule.AcceptOnlyFromAmong(OkfRules.Names);
        var command = new Command("lint", "List where OKF bundles depart from OKF v0.2; exits 1 when there are any findings")
        {
            rule,
            WorkspaceSession.JsonOption,
        };
        command.SetAction(result => WorkspaceSession.Run(result, environment, rebuild: false, session =>
        {
            var bundles = session.Sweep.OkfBundles;
            if (bundles.Count == 0)
            {
                session.Warn("no bundle in links.bundles declares okf_version in its root index.md, so there is nothing to lint");
            }
            foreach (var bundle in bundles.Where(b => b.Version != OkfBundle.SpecVersion))
            {
                var declared = bundle.Version is null ? "an okf_version that is not a version" : $"okf_version {bundle.Version}";
                session.Warn($"{bundle.Root} declares {declared}; hippo reads it as OKF {OkfBundle.SpecVersion}");
            }

            var rules = result.GetValue(rule) is { Length: > 0 } named
                ? named.ToHashSet(StringComparer.Ordinal)
                : OkfRules.Names.Except(session.Workspace.Config.LintOff).ToHashSet(StringComparer.Ordinal);
            var output = FindingQueries.List(session.Db)
                .Where(f => rules.Contains(f.Rule))
                .Select(f => new FindingOutput(f.Rule, f.Path, f.Line, f.Message, JsonSerializer.Deserialize(f.Related, OutputJson.Default.ListString)!))
                .ToList();

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
