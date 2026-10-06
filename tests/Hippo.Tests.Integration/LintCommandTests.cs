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
          "bundles": ["kb"]{{lint}}
        }
        """);

    private const string Declaration = "---\nokf_version: \"0.2\"\n---\n";

    /// <summary>The root index of <see cref="WriteBundle"/>, which links to every page: two as entries, and the rest from
    /// a paragraph, which lists them without entries to check.</summary>
    private const string Listing = """
        # KB

        * [Revenue](metrics/revenue.md) - Revenue
        * [Policy](references/policy.md)

        Also [untyped](untyped.md), [sourced](sourced.md), [footnoted](footnoted.md), [dated](dated.md),
        [attributed](attributed.md) and [lifecycle](lifecycle.md).
        """;

    /// <summary>An OKF bundle that breaks every rule exactly once, beside pages that break none.</summary>
    private void WriteBundle()
    {
        WriteConfig();
        _workspace.Write("kb/index.md", Declaration + Listing);
        _workspace.Write("kb/log.md", "# Log\n\n## 2026-05-22\n* **Update**: Added revenue.\n\n## May 15\n* **Creation**: Started.\n");
        _workspace.Write("kb/metrics/index.md", "---\ntitle: Metrics\n---\n# Metrics\n");
        _workspace.Write("kb/metrics/revenue.md", """
            ---
            type: Metric
            description: Revenue recognized in the period
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
                "okf-index kb/index.md:6",
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
        _workspace.Write("kb/index.md", Declaration + "* [Revenue](metrics/revenue.md) - Revenue recognized in the period\n* [Policy](references/policy.md)\n");

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
        Assert.Equal(7, findings.Count);
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
        Assert.Contains("lint.off: okf-type cannot be turned off; it is a MUST rule in OKF v0.2", result.Stderr);
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
        Assert.Contains("no bundle in bundles declares okf_version", result.Stderr);
    }

    [Fact]
    public void A_bundle_whose_root_index_is_excluded_is_a_plain_bundle()
    {
        WriteBundle();
        _workspace.Write(".hippo/config.json", """{ "files": { "exclude": ["kb/index.md"] }, "bundles": ["kb"] }""");

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
        _workspace.Write("kb/index.md", Listing);
        _workspace.Settle();
        Assert.Empty(Findings(0));

        _workspace.Write("kb/index.md", Declaration + Listing);

        Assert.Equal(9, Findings(1).Count);
    }

    [Fact]
    public void Dropping_the_declaration_clears_the_findings()
    {
        WriteBundle();
        _workspace.Settle();
        Assert.Equal(9, Findings(1).Count);

        _workspace.Write("kb/index.md", "# KB\n");

        Assert.Empty(Findings(0));
    }

    /// <summary>An OKF bundle whose root index drifts from its pages once per kind of drift <c>okf-index</c> reports,
    /// beside entries and pages it leaves alone.</summary>
    private void WriteDrift()
    {
        WriteConfig();
        _workspace.Write("kb/index.md", Declaration + """
            # KB

            * [A](a.md) - Alpha, in short
            * [B](b.md)
            * [C](/c.md) - Gamma
            * [Gone](gone.md) - Gone
            * [D](d.md) - Delta,
              wrapped
            * [Sub](sub/) - A folder
            * [Sub index](sub/index.md) - The sub index
            * [H](h.md) - Eta
            * [Picture](picture.png) - A picture
            * [Site](https://example.com) - Elsewhere
            * [Log](log.md) - Change history
            * [I](i.md) - Iota, folded
            * [J](j.md)
            * [K](k.md)
            * [L](l.md) - 42

            See also [G](g.md).
            """);
        // Links to kb/sub/f.md from a page and from an index that does not cover it, neither of which lists it.
        _workspace.Write("kb/a.md", "---\ntype: Topic\ndescription: Alpha\n---\nSee [F](sub/f.md).\n");
        _workspace.Write("kb/other/index.md", "# Other\n\nSee [F](../sub/f.md).\n");
        _workspace.Write("kb/b.md", "---\ntype: Topic\ndescription: Beta\n---\n");
        _workspace.Write("kb/c.md", "---\ntype: Topic\n---\n");
        _workspace.Write("kb/d.md", "---\ntype: Topic\ndescription: Delta, wrapped\n---\n");
        _workspace.Write("kb/g.md", "---\ntype: Topic\ndescription: Linked from a paragraph\n---\n");
        _workspace.Write("kb/h.md", "---\ntype: [\n---\n");
        _workspace.Write("kb/picture.png", "");
        // Pages whose description reads the same as their entry's once normalized, or is no description at all.
        _workspace.Write("kb/log.md", "# Log\n");
        _workspace.Write("kb/i.md", "---\ntype: Topic\ndescription: >\n  Iota,\n  folded\n---\n");
        _workspace.Write("kb/j.md", "---\ntype: Topic\ndescription:\n---\n");
        _workspace.Write("kb/k.md", "---\ntype: Topic\ndescription: \"\"\n---\n");
        _workspace.Write("kb/l.md", "---\ntype: Topic\ndescription: 42\n---\n");
        _workspace.Write("kb/sub/index.md", "# Sub\n\n* [E](e.md) - Epsilon\n");
        _workspace.Write("kb/sub/e.md", "---\ntype: Topic\ndescription: Epsilon\n---\n");
        _workspace.Write("kb/sub/f.md", "---\ntype: Topic\ndescription: Listed nowhere\n---\n");
    }

    [Fact]
    public void Okf_index_reports_each_kind_of_drift()
    {
        WriteDrift();

        var result = _workspace.Run("lint", "--rule", "okf-index");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "kb/index.md:6  okf-index  entry for kb/a.md does not match the page's description 'Alpha'",
                "kb/index.md:7  okf-index  entry for kb/b.md has no description; the page's is 'Beta'",
                "kb/index.md:8  okf-index  entry for kb/c.md has a description, but the page has none",
                "kb/index.md:9  okf-index  entry links to kb/gone.md, which does not exist",
                "kb/sub/f.md  okf-index  no index above it links to it: kb/sub/index.md, kb/index.md",
            ],
            Lines(result.Stdout));
    }

    [Fact]
    public void An_okf_index_finding_relates_the_page_and_its_indexes()
    {
        WriteDrift();

        var related = Json(_workspace.Run("lint", "--rule", "okf-index", "--json"), 1).EnumerateArray()
            .Select(f => $"{f.GetProperty("path").GetString()}: {string.Join(", ", f.GetProperty("related").EnumerateArray().Select(r => r.GetString()))}")
            .ToList();

        Assert.Equal("kb/index.md: kb/a.md", related[0]);
        Assert.Equal("kb/sub/f.md: kb/sub/index.md, kb/index.md", related[^1]);
    }

    [Fact]
    public void A_page_listed_by_the_index_in_its_own_folder_alone_is_listed()
    {
        WriteDrift();
        _workspace.Write("kb/sub/index.md", "# Sub\n\n* [E](e.md) - Epsilon\n* [F](f.md) - Listed nowhere\n");

        Assert.DoesNotContain(Findings(1, "--rule", "okf-index"), f => f.StartsWith("okf-index kb/sub/f.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Changing_a_page_description_reports_drift_though_the_index_did_not_change()
    {
        WriteConfig();
        _workspace.Write("kb/index.md", Declaration + "* [A](a.md) - Alpha\n");
        _workspace.Write("kb/a.md", "---\ntype: Topic\ndescription: Alpha\n---\n");
        _workspace.Settle();
        Assert.Empty(Findings(0, "--rule", "okf-index"));

        _workspace.Write("kb/a.md", "---\ntype: Topic\ndescription: Alpha, revised\n---\n");
        Assert.Equal(["okf-index kb/index.md:4"], Findings(1, "--rule", "okf-index"));

        File.Delete(_workspace.Combine("kb/a.md"));
        var result = _workspace.Run("lint", "--rule", "okf-index");
        Assert.Equal(["kb/index.md:4  okf-index  entry links to kb/a.md, which does not exist"], Lines(result.Stdout));
    }

    [Fact]
    public void A_page_in_a_plain_bundle_nested_in_an_okf_one_need_not_be_listed()
    {
        WriteConfig();
        _workspace.Write(".hippo/config.json", """{ "bundles": ["kb", "kb/plain"] }""");
        _workspace.Write("kb/index.md", Declaration + "# KB\n");
        _workspace.Write("kb/plain/a.md", "# A\n");

        Assert.Empty(Findings(0, "--rule", "okf-index"));
    }

    /// <summary>OKF's path fields, each written as the spec's examples write them.</summary>
    private void WriteComputation(string config = "")
    {
        _workspace.Write(".hippo/config.json", $$"""{ "bundles": ["kb"]{{config}} }""");
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

        var broken = Json(_workspace.Run("lint", "--rule", "broken-link", "--json"), 1).EnumerateArray().Select(f => f.GetProperty("message").GetString());

        Assert.Contains("all queries in BigQuery project X -> kb/all queries in BigQuery project X", broken);
    }

    [Fact]
    public void A_frontmatter_entry_for_an_okf_field_overrides_how_it_resolves()
    {
        WriteComputation(""", "links": { "frontmatter": [{ "field": "sources[].resource", "resolve": "page" }] }""");

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

    /// <summary>A small workspace in the notes repo's shape: a wiki bundle, not an OKF one, whose pages cite raw files
    /// from frontmatter.</summary>
    private void WriteNotes()
    {
        _workspace.Write(".hippo/config.json", """
            {
              "bundles": ["wiki"],
              "links": {
                "frontmatter": [{ "field": "sources[].resource", "resolve": "bundle" }]
              }
            }
            """);
        _workspace.Write("wiki/index.md", "# Index\n\n- [Topic](topics/topic.md)\n");
        _workspace.Write("wiki/topics/topic.md", """
            ---
            title: Topic
            sources:
              - id: j-2026-09-01
                resource: ../raw/journal/2026-09-01.md
              - id: j-gone
                resource: ../raw/journal/gone.md
            ---
            # Topic

            See [the index](/index.md), [[Wikilink]] and [the web](https://example.com).
            """);
        _workspace.Write("raw/journal/2026-09-01.md", "# Day\n\n![photo](img/photo%201.png)\n");
        _workspace.Write("raw/journal/img/photo 1.png", "png");
    }

    private TestWorkspace.Result BrokenLinks() => _workspace.Run("lint", "--rule", "broken-link");

    [Fact]
    public void Broken_link_lists_links_to_missing_files_and_exits_1()
    {
        WriteNotes();

        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:7  broken-link  ../raw/journal/gone.md -> raw/journal/gone.md"], Lines(result.Stdout));
    }

    [Fact]
    public void A_broken_link_finding_names_the_link_and_relates_its_target()
    {
        WriteNotes();
        _workspace.Write("wiki/escape.md", "[out](../../outside.md)\n");

        var json = Json(_workspace.Run("lint", "--rule", "broken-link", "--json"), 1);

        Assert.Equal(
            [
                "broken-link wiki/escape.md 1 ../../outside.md -> outside the workspace []",
                "broken-link wiki/topics/topic.md 7 ../raw/journal/gone.md -> raw/journal/gone.md [raw/journal/gone.md]",
            ],
            json.EnumerateArray().Select(f =>
                $"{f.GetProperty("rule").GetString()} {f.GetProperty("path").GetString()} {f.GetProperty("line").GetInt32()} {f.GetProperty("message").GetString()} [{string.Join(", ", f.GetProperty("related").EnumerateArray().Select(r => r.GetString()))}]"));
    }

    [Fact]
    public void Creating_a_missing_target_fixes_its_links_on_the_next_command()
    {
        WriteNotes();
        _workspace.Run("index");

        _workspace.Write("raw/journal/gone.md", "# Back\n");
        var result = BrokenLinks();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void Deleting_a_target_breaks_its_links_on_the_next_command()
    {
        WriteNotes();
        _workspace.Run("index");

        File.Delete(_workspace.Combine("raw/journal/2026-09-01.md"));
        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("wiki/topics/topic.md:5  broken-link  ../raw/journal/2026-09-01.md -> raw/journal/2026-09-01.md", Lines(result.Stdout));
    }

    [Fact]
    public void A_footnote_reference_is_not_a_broken_link_but_a_broken_link_in_a_definition_is()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("raw/x.md", "# Day\n");
        _workspace.Write("wiki/topics/a.md", """
            A claim.[^j-2026-09-16] Another.[^1]

            [^j-2026-09-16]: [Day](<../../raw/x.md>)
            [^1]: foo
            [^aside]: [Gone](<../../raw/gone.md>)
            """);

        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/a.md:5  broken-link  ../../raw/gone.md -> raw/gone.md"], Lines(result.Stdout));
    }

    [Fact]
    public void A_clean_workspace_exits_0_from_broken_link()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[a](a.md)\n");
        _workspace.Write("a.md", "[index](index.md)\n");

        var result = BrokenLinks();

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
    }

    [Fact]
    public void A_link_into_the_roots_hippo_folder_is_broken()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[config](.hippo/config.json)\n");

        var result = BrokenLinks();

        Assert.Equal(["index.md:1  broken-link  .hippo/config.json -> .hippo/config.json"], Lines(result.Stdout));
    }

    [Fact]
    public void A_link_to_a_folder_holding_an_indexed_file_is_a_directory_and_not_broken()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("wiki/index.md", "[home](../) [2021](../raw/2021/) [2021](../raw/2021) [empty](../raw/empty/)\n");
        _workspace.Write("raw/2021/day.md", "# Day\n");
        Directory.CreateDirectory(_workspace.Combine("raw/empty"));

        var refs = _workspace.Run("refs", "wiki/index.md");
        var broken = BrokenLinks();

        Assert.Equal(
            [
                "1  body         directory  ../  [home]", "1  body         directory  raw/2021  [2021]", "1  body         directory  raw/2021  [2021]",
                "1  body         missing    raw/empty  [empty]",
            ],
            Lines(refs.Stdout));
        Assert.Equal(1, broken.ExitCode);
        Assert.Equal(["wiki/index.md:1  broken-link  ../raw/empty/ -> raw/empty"], Lines(broken.Stdout));
    }

    [Fact]
    public void A_folder_link_breaks_once_the_last_indexed_file_under_it_is_gone()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[2021](raw/2021/)\n");
        _workspace.Write("raw/2021/01/day.md", "# Day\n");
        _workspace.Run("index");

        File.Delete(_workspace.Combine("raw/2021/01/day.md"));
        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  broken-link  raw/2021/ -> raw/2021"], Lines(result.Stdout));
    }

    [Fact]
    public void Broken_link_names_a_link_that_leaves_the_workspace_as_such()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("index.md", "[out](../outside.md)\n");

        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["index.md:1  broken-link  ../outside.md -> outside the workspace"], Lines(result.Stdout));
    }

    [Fact]
    public void Rule_broken_link_lists_only_broken_links()
    {
        WriteBundle();
        _workspace.Write("notes/free.md", "See [nothing](nothing.md).\n");

        Assert.Equal(["broken-link notes/free.md:1"], Findings(1, "--rule", "broken-link"));
    }

    [Fact]
    public void Broken_links_fall_in_path_and_line_order_among_the_stored_findings()
    {
        WriteBundle();
        _workspace.Write("kb/attributed.md", "---\ntype: Topic\ngenerated: { by: claude, at: 2026-06-20T22:53:05Z }\n---\nSee [gone](gone.md).\n");

        Assert.Equal(
            ["okf-actor kb/attributed.md:3", "broken-link kb/attributed.md:5", "okf-status kb/lifecycle.md:3"],
            Findings(1, "--rule", "okf-actor", "--rule", "broken-link", "--rule", "okf-status"));
    }

    [Fact]
    public void Lint_off_turns_off_broken_link()
    {
        WriteBundle();
        _workspace.Write("notes/free.md", "See [nothing](nothing.md).\n");
        Assert.Contains("broken-link notes/free.md:1", Findings(1));

        WriteConfig(""", "lint": { "off": ["broken-link"] }""");

        Assert.DoesNotContain(Findings(1), f => f.StartsWith("broken-link", StringComparison.Ordinal));
    }

    [Fact]
    public void A_workspace_with_no_okf_bundle_reports_broken_links_without_the_bundle_warning()
    {
        WriteNotes();

        var result = BrokenLinks();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:7  broken-link  ../raw/journal/gone.md -> raw/journal/gone.md"], Lines(result.Stdout));
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void A_plain_lint_with_no_okf_bundle_reports_broken_links_and_scopes_the_bundle_warning_to_okf()
    {
        WriteNotes();

        var result = _workspace.Run("lint");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["wiki/topics/topic.md:7  broken-link  ../raw/journal/gone.md -> raw/journal/gone.md"], Lines(result.Stdout));
        Assert.Equal("hippo: warning: no bundle in bundles declares okf_version in its root index.md, so there is no OKF bundle to check", result.Stderr.Trim());
    }

    [Fact]
    public void A_plain_lint_of_a_workspace_with_no_bundles_says_nothing_about_okf()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("notes/fine.md", "# Fine\n");

        var result = _workspace.Run("lint");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void Frontmatter_that_fails_to_parse_outside_any_bundle_is_one_finding_and_exits_1()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("notes/broken.md", "---\n- not\n- a mapping\n---\n# Broken\n");
        _workspace.Write("notes/fine.md", "---\ntitle: Fine\n---\n# Fine\n");

        var finding = Assert.Single(Json(_workspace.Run("lint", "--json"), 1).EnumerateArray());

        Assert.Equal(("frontmatter-syntax", "notes/broken.md", JsonValueKind.Null, "frontmatter is not a mapping", 0),
            (finding.GetProperty("rule").GetString(), finding.GetProperty("path").GetString(), finding.GetProperty("line").ValueKind,
                finding.GetProperty("message").GetString(), finding.GetProperty("related").GetArrayLength()));
    }

    [Fact]
    public void A_frontmatter_syntax_finding_prints_the_path_and_the_error()
    {
        _workspace.Write(".hippo/config.json", "");
        _workspace.Write("notes/broken.md", "---\n- not\n- a mapping\n---\n# Broken\n");

        var result = _workspace.Run("lint", "--rule", "frontmatter-syntax");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["notes/broken.md  frontmatter-syntax  frontmatter is not a mapping"], Lines(result.Stdout));
    }

    [Fact]
    public void Lint_off_turns_off_frontmatter_syntax()
    {
        WriteBundle();
        _workspace.Write("notes/free.md", "---\ntitle: [\n---\n");
        Assert.Contains("frontmatter-syntax notes/free.md:", Findings(1));

        WriteConfig(""", "lint": { "off": ["frontmatter-syntax"] }""");

        Assert.DoesNotContain(Findings(1), f => f.StartsWith("frontmatter-syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void A_concept_whose_frontmatter_fails_to_parse_gets_okf_type_and_frontmatter_syntax()
    {
        WriteBundle();
        _workspace.Write("kb/untyped.md", "---\ntype: [\n---\n");

        Assert.Equal(
            ["okf-type kb/untyped.md:", "frontmatter-syntax kb/untyped.md:"],
            Findings(1, "--rule", "okf-type", "--rule", "frontmatter-syntax"));
    }

    [Fact]
    public void A_concept_whose_frontmatter_fails_to_parse_still_gets_okf_type_with_frontmatter_syntax_off()
    {
        WriteBundle();
        WriteConfig(""", "lint": { "off": ["frontmatter-syntax"] }""");
        _workspace.Write("kb/untyped.md", "---\ntype: [\n---\n");

        var findings = Findings(1);

        Assert.Contains("okf-type kb/untyped.md:", findings);
        Assert.DoesNotContain(findings, f => f.StartsWith("frontmatter-syntax", StringComparison.Ordinal));
    }

    [Fact]
    public void A_root_index_or_log_whose_frontmatter_fails_to_parse_gets_only_frontmatter_syntax()
    {
        WriteBundle();
        _workspace.Write("kb/log.md", "---\ntitle: [\n---\n# Log\n");

        Assert.Equal(["frontmatter-syntax kb/log.md:"],
            Findings(1, "--rule", "okf-type", "--rule", "frontmatter-syntax").Where(f => f.EndsWith(" kb/log.md:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rule_frontmatter_syntax_reports_the_files_find_errors_lists()
    {
        WriteBundle();
        _workspace.Write("kb/broken.md", "---\ntype: [\n---\n");
        _workspace.Write("notes/a.md", "---\na: [\n---\n");
        _workspace.Write("notes/b.md", "---\n- not\n- a mapping\n---\n");

        var errors = Json(_workspace.Run("find", "--errors", "--json"), 0).EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        var findings = Json(_workspace.Run("lint", "--rule", "frontmatter-syntax", "--json"), 1).EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()).ToList();

        Assert.Equal(["kb/broken.md", "notes/a.md", "notes/b.md"], errors);
        Assert.Equal(errors, findings);
    }

    [Fact]
    public void A_bundle_that_declares_another_version_is_not_noted_without_an_okf_rule()
    {
        WriteBundle();
        _workspace.Write("kb/index.md", "---\nokf_version: \"0.3\"\n---\n# KB\n");

        var result = BrokenLinks();

        Assert.Equal((0, ""), (result.ExitCode, result.Stderr));
    }

    /// <summary>A frozen archive whose page breaks two rules, and a note that links into it.</summary>
    private void WriteArchive(string note)
    {
        _workspace.Write(".hippo/config.json", """{ "lint": { "exclude": ["archive/**"] } }""");
        _workspace.Write("archive/old.md", "---\n- not\n- a mapping\n---\n[gone](gone.md)\n");
        _workspace.Write("notes/a.md", note);
    }

    [Fact]
    public void Lint_exclude_leaves_out_the_findings_on_files_it_matches_but_not_on_links_into_them()
    {
        WriteArchive("[old](../archive/old.md)\n[missing](../archive/missing.md)\n");

        Assert.Equal(["broken-link notes/a.md:2"], Findings(1));
    }

    [Fact]
    public void A_file_lint_exclude_matches_stays_indexed_and_a_link_target()
    {
        WriteArchive("[old](../archive/old.md)\n");

        var refs = Json(_workspace.Run("refs", "notes/a.md", "--json"), 0);

        Assert.Equal("file", Assert.Single(refs.EnumerateArray()).GetProperty("type").GetString());
        Assert.Equal(["archive/old.md", "notes/a.md"], Lines(_workspace.Run("find").Stdout));
    }

    [Fact]
    public void Findings_lint_exclude_leaves_out_do_not_count_toward_the_exit_status()
    {
        WriteArchive("# A\n");

        var result = _workspace.Run("lint");

        Assert.Equal((0, ""), (result.ExitCode, result.Stdout));
    }

    [Fact]
    public void Lint_exclude_leaves_out_stored_and_worked_okf_findings_on_files_it_matches()
    {
        WriteBundle();
        WriteConfig(""", "lint": { "exclude": ["kb/metrics/**", "kb/log.md", "kb/index.md"] }""");

        Assert.Equal(
            [
                "okf-actor kb/attributed.md:3",
                "okf-timestamp kb/dated.md:3",
                "okf-footnote kb/footnoted.md:6",
                "okf-status kb/lifecycle.md:3",
                "okf-source-resource kb/sourced.md:4",
                "okf-type kb/untyped.md:",
            ],
            Findings(1));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("archive/*")]
    [InlineData("**/archive")]
    public void A_lint_exclude_glob_that_matches_a_folder_covers_everything_under_it_as_in_files_exclude(string glob)
    {
        _workspace.Write(".hippo/config.json", $$"""{ "lint": { "exclude": ["{{glob}}"] } }""");
        _workspace.Write("archive/old.md", "[gone](gone.md)\n");
        _workspace.Write("archive/sub/deep.md", "[gone](gone.md)\n");

        Assert.Empty(Findings(0));
    }

    [Fact]
    public void Rule_still_leaves_out_the_findings_lint_exclude_matches()
    {
        WriteArchive("# A\n");

        Assert.Empty(Findings(0, "--rule", "frontmatter-syntax"));
    }

    [Fact]
    public void A_change_to_lint_exclude_applies_on_the_next_run_without_parsing_pages_again()
    {
        WriteArchive("# A\n");
        Assert.Empty(Findings(0));
        _workspace.Settle();

        _workspace.Write(".hippo/config.json", "");
        var result = _workspace.Run("lint", "--json");

        Assert.Equal(["frontmatter-syntax", "broken-link"],
            Json(result, 1).EnumerateArray().Select(f => f.GetProperty("rule").GetString()));
        Assert.DoesNotContain("cannot read the frontmatter", result.Stderr);
    }

    [Fact]
    public void Help_lists_every_rule_with_a_description()
    {
        string[] rules =
        [
            "okf-type", "okf-index-frontmatter", "okf-log-date", "okf-source-resource", "okf-footnote", "okf-timestamp",
            "okf-actor", "okf-status", "okf-index", "broken-link", "frontmatter-syntax",
        ];

        var lines = Lines(_workspace.Run("lint", "--help").Stdout);

        foreach (var rule in rules)
        {
            // The rule's name, then its description on the same line.
            Assert.Single(lines, line => line.StartsWith(rule + " ", StringComparison.Ordinal) && line.Length > rule.Length + 20);
        }
    }

    [Fact]
    public void Help_says_which_rules_lint_off_can_turn_off()
    {
        var text = Words(_workspace.Run("lint", "--help").Stdout);

        Assert.Contains("lint.off can turn off every rule but okf-type, okf-index-frontmatter and okf-log-date", text);
    }

    [Fact]
    public void Help_lists_the_rules_after_lints_options()
    {
        var result = _workspace.Run("lint", "--help");

        Assert.Equal(0, result.ExitCode);
        var options = result.Stdout.IndexOf("Options:", StringComparison.Ordinal);
        Assert.InRange(options, 0, result.Stdout.IndexOf("Rules:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("find")]
    public void Only_lints_help_lists_the_rules(params string[] command)
    {
        var result = _workspace.Run([.. command, "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Rules:", result.Stdout);
        Assert.DoesNotContain("lint.off can turn off", result.Stdout);
    }

    [Fact]
    public void An_unknown_rule_prints_the_error_and_a_pointer_to_help_not_the_help()
    {
        var result = _workspace.Run("lint", "--rule", "no-such-rule");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Argument 'no-such-rule' not recognized", result.Stderr);
        Assert.Equal("Run 'hippo lint --help' for usage.", Lines(result.Stderr)[^1]);
        Assert.DoesNotContain("Usage:", result.Stdout + result.Stderr);
        Assert.DoesNotContain("Options:", result.Stdout + result.Stderr);
    }

    /// <summary>Help text with each run of whitespace read as one space, so where it wraps does not matter.</summary>
    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
