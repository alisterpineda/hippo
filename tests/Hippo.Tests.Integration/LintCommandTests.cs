using System.Text.Json;

namespace Hippo.Tests.Integration;

public sealed class LintCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result, int exitCode)
    {
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Each finding as <c>rule path:line</c>.</summary>
    private List<string> Findings(int exitCode, params string[] args) =>
        Json(_workspace.Run(["lint", .. args, "--json"]), exitCode).EnumerateArray()
            .Select(f => $"{f.GetProperty("rule").GetString()} {f.GetProperty("path").GetString()}:{f.GetProperty("line")}")
            .ToList();

    private void WriteConfig(string lint = "") => _workspace.Write(".hippo/config.json", $$"""
        {
          "links": { "bundles": ["kb"] }{{lint}}
        }
        """);

    /// <summary>An OKF bundle that breaks every rule exactly once, beside pages that break none.</summary>
    private void WriteBundle()
    {
        WriteConfig();
        _workspace.Write("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# KB\n\n* [Revenue](metrics/revenue.md) - Revenue\n");
        _workspace.Write("kb/log.md", "# Log\n\n## 2026-05-22\n* **Update**: Added revenue.\n\n## May 15\n* **Creation**: Started.\n");
        _workspace.Write("kb/metrics/index.md", "---\ntitle: Metrics\n---\n# Metrics\n");
        _workspace.Write("kb/metrics/revenue.md", """
            ---
            type: Metric
            status: stable
            generated: { by: reference_agent/gemini-2.5-pro, at: 2026-06-20T22:53:05Z }
            sources:
              - id: policy
                resource: references/policy.md
            ---
            Revenue per the policy.[^policy]

            [^policy]: Revenue recognition policy
            """);
        _workspace.Write("kb/references/policy.md", "---\ntype: Reference\n---\n# Policy\n");
        _workspace.Write("kb/untyped.md", "---\ntitle: Untyped\n---\n");
        _workspace.Write("kb/sourced.md", "---\ntype: Topic\nsources:\n  - id: x\n---\n");
        _workspace.Write("kb/footnoted.md", "---\ntype: Topic\n---\nA claim.[^1]\n\n[^1]: A note\n");
        _workspace.Write("kb/dated.md", "---\ntype: Topic\nstale_after: 2026-09-23\n---\n");
        _workspace.Write("kb/attributed.md", "---\ntype: Topic\ngenerated: { by: claude, at: 2026-06-20T22:53:05Z }\n---\n");
        _workspace.Write("kb/lifecycle.md", "---\ntype: Topic\nstatus: final\n---\n");
        _workspace.Write("notes/free.md", "No frontmatter, and outside any bundle.\n");
    }

    [Fact]
    public void A_bundle_with_one_violation_per_rule_reports_exactly_those()
    {
        WriteBundle();

        Assert.Equal(
            [
                "okf-actor kb/attributed.md:3",
                "okf-timestamp kb/dated.md:3",
                "okf-footnote kb/footnoted.md:6",
                "okf-status kb/lifecycle.md:3",
                "okf-log-date kb/log.md:6",
                "okf-index-frontmatter kb/metrics/index.md:1",
                "okf-source-resource kb/sourced.md:4",
                "okf-type kb/untyped.md:",
            ],
            Findings(1));
    }

    [Fact]
    public void Lint_prints_one_finding_per_line()
    {
        WriteBundle();

        var result = _workspace.Run("lint", "--rule", "okf-type", "--rule", "okf-status");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "kb/lifecycle.md:3  okf-status  status 'final' is not draft, stable or deprecated",
                "kb/untyped.md  okf-type  its frontmatter has no type",
            ],
            Lines(result.Stdout));
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void A_conformant_bundle_exits_0()
    {
        WriteBundle();
        foreach (var page in new[] { "log.md", "metrics/index.md", "untyped.md", "sourced.md", "footnoted.md", "dated.md", "attributed.md", "lifecycle.md" })
        {
            File.Delete(_workspace.Combine($"kb/{page}"));
        }

        var result = _workspace.Run("lint");

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stdout}{result.Stderr}");
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public void Lint_off_hides_a_should_rule()
    {
        WriteBundle();
        WriteConfig(""", "lint": { "off": ["okf-footnote", "okf-status"] }""");

        var findings = Findings(1);

        Assert.DoesNotContain(findings, f => f.StartsWith("okf-footnote", StringComparison.Ordinal) || f.StartsWith("okf-status", StringComparison.Ordinal));
        Assert.Equal(6, findings.Count);
    }

    [Fact]
    public void Rule_lists_only_the_rules_it_names_even_one_lint_off_turns_off()
    {
        WriteBundle();
        WriteConfig(""", "lint": { "off": ["okf-footnote"] }""");

        Assert.Equal(["okf-footnote kb/footnoted.md:6"], Findings(1, "--rule", "okf-footnote"));
    }

    [Fact]
    public void Rule_with_nothing_to_report_exits_0()
    {
        WriteBundle();
        File.Delete(_workspace.Combine("kb/lifecycle.md"));

        Assert.Empty(Findings(0, "--rule", "okf-status"));
    }

    [Fact]
    public void An_unknown_rule_is_a_usage_error()
    {
        WriteBundle();

        Assert.Equal(2, _workspace.Run("lint", "--rule", "okf-nope").ExitCode);
    }

    [Fact]
    public void Lint_off_naming_a_must_rule_is_refused()
    {
        WriteBundle();
        WriteConfig(""", "lint": { "off": ["okf-type"] }""");

        var result = _workspace.Run("lint");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("lint.off: okf-type is a MUST rule, which cannot be turned off", result.Stderr);
    }

    [Fact]
    public void A_bundle_that_declares_another_version_is_read_as_0_2_with_a_note()
    {
        WriteBundle();
        _workspace.Write("kb/index.md", "---\nokf_version: \"0.3\"\n---\n# KB\n");

        var result = _workspace.Run("lint", "--rule", "okf-type");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["kb/untyped.md  okf-type  its frontmatter has no type"], Lines(result.Stdout));
        Assert.Equal("hippo: warning: kb declares okf_version 0.3; hippo reads it as OKF 0.2", result.Stderr.Trim());
    }

    [Fact]
    public void A_bundle_that_declares_a_version_that_is_not_a_value_is_read_as_0_2_with_a_note()
    {
        WriteBundle();
        _workspace.Write("kb/index.md", "---\nokf_version: [0.2]\n---\n# KB\n");

        var result = _workspace.Run("lint", "--rule", "okf-type");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["kb/untyped.md  okf-type  its frontmatter has no type"], Lines(result.Stdout));
        Assert.Equal("hippo: warning: kb declares an okf_version that is not a version; hippo reads it as OKF 0.2", result.Stderr.Trim());
    }

    [Theory]
    [InlineData("# KB\n")]
    [InlineData("---\nokf_version:\n---\n# KB\n")]
    [InlineData("---\nokf_version: ~\n---\n# KB\n")]
    public void A_bundle_without_the_declaration_is_a_plain_bundle(string index)
    {
        WriteBundle();
        _workspace.Write("kb/index.md", index);

        var result = _workspace.Run("lint");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("no bundle in links.bundles declares okf_version", result.Stderr);
    }

    [Fact]
    public void A_bundle_whose_root_index_is_excluded_is_a_plain_bundle()
    {
        WriteBundle();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": ["kb/index.md"] }, "links": { "bundles": ["kb"] } }""");

        Assert.Empty(Findings(0));
    }

    [Fact]
    public void A_folder_not_listed_as_a_bundle_is_not_linted()
    {
        WriteBundle();
        _workspace.Write(".hippo/config.json", "");

        Assert.Empty(Findings(0));
    }

    [Fact]
    public void Fixing_a_page_clears_its_finding()
    {
        WriteBundle();
        Assert.Contains("okf-status kb/lifecycle.md:3", Findings(1));

        _workspace.Write("kb/lifecycle.md", "---\ntype: Topic\nstatus: deprecated\n---\n");

        Assert.DoesNotContain("okf-status kb/lifecycle.md:3", Findings(1));
    }

    [Fact]
    public void Declaring_okf_version_later_lints_pages_that_did_not_change()
    {
        WriteBundle();
        _workspace.Write("kb/index.md", "# KB\n");
        _workspace.Settle();
        Assert.Empty(Findings(0));

        _workspace.Write("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# KB\n");

        Assert.Equal(8, Findings(1).Count);
    }

    [Fact]
    public void Dropping_the_declaration_clears_the_findings()
    {
        WriteBundle();
        _workspace.Settle();
        Assert.Equal(8, Findings(1).Count);

        _workspace.Write("kb/index.md", "# KB\n");

        Assert.Empty(Findings(0));
    }

    /// <summary>OKF's path fields, each written as the spec's examples write them.</summary>
    private void WriteComputation(string config = "")
    {
        _workspace.Write(".hippo/config.json", $$"""{ "links": { "bundles": ["kb"]{{config}} } }""");
        _workspace.Write("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# KB\n");
        _workspace.Write("kb/computations/revenue.md", """
            ---
            type: Attested Computation
            resource: https://console.cloud.google.com/bigquery?p=acme
            runtime: bigquery
            computation: references/computations/revenue.sql
            executor:
              resource: references/skills/run-on-bq.md
            attester:
              resource: /references/attesters/revenue.py
            sources:
              - resource: ../raw/policy.md
              - resource: all queries in BigQuery project X
            ---
            See [the metric](../metrics/revenue.md).
            """);
    }

    private List<string> Refs(string path) =>
        Json(_workspace.Run("refs", path, "--json"), 0).EnumerateArray()
            .Select(l => $"{l.GetProperty("line").GetInt32()} {l.GetProperty("kind").GetString()} {l.GetProperty("type").GetString()} {l.GetProperty("target").GetString()}")
            .ToList();

    [Fact]
    public void Okf_path_fields_are_links_that_resolve_against_the_bundle_root()
    {
        WriteComputation();
        _workspace.Write("kb/references/skills/run-on-bq.md", "---\ntype: Reference\n---\n");
        _workspace.Write("raw/policy.md", "# Policy\n");

        Assert.Equal(
            [
                "3 frontmatter url ",
                "5 frontmatter missing kb/references/computations/revenue.sql",
                "7 frontmatter file kb/references/skills/run-on-bq.md",
                "9 frontmatter missing kb/references/attesters/revenue.py",
                "11 frontmatter file raw/policy.md",
                "12 frontmatter missing kb/all queries in BigQuery project X",
                "14 body missing kb/metrics/revenue.md",
            ],
            Refs("kb/computations/revenue.md"));
    }

    [Fact]
    public void A_scope_descriptor_is_reported_as_broken()
    {
        WriteComputation();

        var broken = Json(_workspace.Run("broken", "--json"), 1).EnumerateArray().Select(l => l.GetProperty("raw").GetString());

        Assert.Contains("all queries in BigQuery project X", broken);
    }

    [Fact]
    public void A_frontmatter_entry_for_an_okf_field_overrides_how_it_resolves()
    {
        WriteComputation(""", "frontmatter": [{ "field": "sources[].resource", "resolve": "page" }]""");

        var refs = Refs("kb/computations/revenue.md");

        Assert.Contains("11 frontmatter missing kb/raw/policy.md", refs);
        Assert.Single(refs, r => r.StartsWith("11 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_bundle_has_no_okf_path_links()
    {
        WriteComputation();
        _workspace.Write("kb/index.md", "# KB\n");

        Assert.Equal(["14 body missing kb/metrics/revenue.md"], Refs("kb/computations/revenue.md"));
    }
}
