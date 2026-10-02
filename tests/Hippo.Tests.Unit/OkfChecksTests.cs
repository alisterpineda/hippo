using Hippo.Okf;
using Hippo.Workspaces;

namespace Hippo.Tests.Unit;

public class OkfChecksTests
{
    private static readonly PageSettings Okf = new(["kb"], LinkSettings.Default, ["kb"]);

    private static readonly PageSettings Plain = new(["kb"], LinkSettings.Default, []);

    private static List<Finding> Findings(string path, string text, PageSettings? settings = null) =>
        Page.Parse(path, text, settings ?? Okf).Findings;

    private static List<string> Lines(string path, string text) =>
        Findings(path, text).Select(f => $"{f.Line} {f.Rule}: {f.Message}").ToList();

    /// <summary>A concept that breaks no rule, to build variations on.</summary>
    private const string Clean = """
        ---
        type: Metric
        status: stable
        generated: { by: reference_agent/gemini-2.5-pro, at: 2026-06-20T22:53:05Z }
        verified:
          - { by: human:ahormati, at: 2026-06-25T09:00:00+02:00 }
          - { by: process:finance-nightly, at: 2026-06-26T02:00:00.5Z }
        stale_after: 2026-09-23T00:00:00Z
        sources:
          - id: rev-policy
            resource: https://wiki.acme/finance/revenue-recognition
            last_modified: 2026-04-02T00:00:00Z
            usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30T00:00:00Z }
        usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30T00:00:00Z }
        ---
        Revenue per the policy.[^rev-policy]

        [^rev-policy]: Revenue recognition policy
        """;

    [Fact]
    public void A_conformant_concept_has_no_findings()
    {
        Assert.Empty(Findings("kb/metrics/revenue.md", Clean));
    }

    [Fact]
    public void A_page_outside_an_okf_bundle_is_not_checked()
    {
        Assert.Empty(Findings("notes/a.md", "# No frontmatter\n"));
        Assert.Empty(Findings("kb/a.md", "# No frontmatter\n", Plain));
    }

    [Fact]
    public void A_page_in_a_plain_bundle_nested_in_an_okf_one_is_not_checked()
    {
        var settings = new PageSettings(["kb", "kb/plain"], LinkSettings.Default, ["kb"]);

        Assert.Empty(Findings("kb/plain/a.md", "# No frontmatter\n", settings));
        Assert.Single(Findings("kb/a.md", "# No frontmatter\n", settings));
    }

    [Theory]
    [InlineData("# Title\n", "it has no frontmatter")]
    [InlineData("---\n---\n", "its frontmatter has no type")]
    [InlineData("---\ntitle: A\n---\n", "its frontmatter has no type")]
    [InlineData("---\ntype: a\ntype: b\n---\n", "its frontmatter cannot be parsed: line 3: ")]
    public void A_concept_without_a_type_is_reported_as_a_whole(string text, string message)
    {
        var finding = Assert.Single(Findings("kb/a.md", text));

        Assert.Equal((OkfRules.Type, (int?)null), (finding.Rule, finding.Line));
        Assert.StartsWith(message, finding.Message);
    }

    [Theory]
    [InlineData("type:")]
    [InlineData("type: ''")]
    [InlineData("type: '  '")]
    [InlineData("type: [Metric]")]
    public void An_empty_type_is_reported_on_its_line(string field)
    {
        Assert.Equal(["3 okf-type: type is empty"], Lines("kb/a.md", $"---\ntitle: A\n{field}\n---\n"));
    }

    [Fact]
    public void A_page_whose_frontmatter_cannot_be_parsed_has_no_other_findings()
    {
        var finding = Assert.Single(Findings("kb/a.md", "---\ntype: [a\n---\nA claim.[^x]\n\n[^x]: Note\n"));

        Assert.Equal(OkfRules.Type, finding.Rule);
    }

    [Theory]
    [InlineData("kb/index.md")]
    [InlineData("kb/sub/index.md")]
    [InlineData("kb/log.md")]
    [InlineData("kb/sub/log.md")]
    public void Reserved_files_are_not_concepts(string path)
    {
        Assert.DoesNotContain(Findings(path, "# Title\n"), f => f.Rule == OkfRules.Type);
    }

    [Fact]
    public void A_name_that_only_resembles_a_reserved_one_is_a_concept()
    {
        Assert.Equal(OkfRules.Type, Assert.Single(Findings("kb/Index.md", "# Title\n")).Rule);
    }

    [Fact]
    public void The_root_index_may_hold_only_okf_version()
    {
        Assert.Empty(Findings("kb/index.md", "---\nokf_version: \"0.2\"\n---\n# Index\n"));
        Assert.Equal(
            ["3 okf-index-frontmatter: the bundle root's index.md may hold only okf_version in its frontmatter, not title"],
            Lines("kb/index.md", "---\nokf_version: \"0.2\"\ntitle: Index\n---\n# Index\n"));
    }

    [Theory]
    [InlineData("---\ntitle: Sub\n---\n# Sub\n")]
    [InlineData("---\n---\n# Sub\n")]
    [InlineData("---\ntitle: [a\n---\n# Sub\n")]
    public void An_index_below_the_root_has_no_frontmatter(string text)
    {
        Assert.Equal(["1 okf-index-frontmatter: an index.md below the bundle root has frontmatter"], Lines("kb/sub/index.md", text));
    }

    [Fact]
    public void An_index_below_the_root_without_frontmatter_has_no_findings()
    {
        Assert.Empty(Findings("kb/sub/index.md", "# Sub\n\n* [A](a.md) - a page\n"));
    }

    [Fact]
    public void Each_level_2_heading_in_a_log_is_a_date()
    {
        var log = """
            # Directory Update Log

            ## 2026-05-22
            * **Update**: Added a table.

            ### Details

            ## 22 May 2026
            * **Creation**: Made a playbook.

            ## 2026-02-30

            ## `2026-05-15`
            """;

        Assert.Equal(
            [
                "8 okf-log-date: date heading '22 May 2026' is not a YYYY-MM-DD date",
                "11 okf-log-date: date heading '2026-02-30' is not a YYYY-MM-DD date",
            ],
            Lines("kb/sub/log.md", log));
    }

    [Fact]
    public void Log_lines_count_from_the_top_of_the_file()
    {
        Assert.Equal(["5 okf-log-date: date heading 'May' is not a YYYY-MM-DD date"],
            Lines("kb/log.md", "---\nscope: kb\n---\n# Log\n## May\n"));
    }

    [Fact]
    public void A_source_without_a_resource_is_reported()
    {
        var text = """
            ---
            type: Metric
            sources:
              - id: a
                title: No resource
              - id: b
                resource:
              - just a string
              - id: c
                resource: https://example.com
            ---
            """;

        Assert.Equal(
            [
                "4 okf-source-resource: sources entry has no resource",
                "6 okf-source-resource: sources entry has no resource",
                "8 okf-source-resource: sources entry has no resource",
            ],
            Lines("kb/a.md", text));
    }

    [Fact]
    public void A_footnote_whose_label_is_no_source_id_is_reported()
    {
        var text = """
            ---
            type: Topic
            sources:
              - id: known
                resource: raw/a.md
            ---
            One.[^known] Two.[^1]

            [^known]: Cited
            [^1]: [A file](raw/b.md), linked straight
            """;

        Assert.Equal(["10 okf-footnote: footnote [^1] matches no sources[].id"], Lines("kb/a.md", text));
    }

    [Fact]
    public void A_footnote_label_matches_a_source_id_by_case()
    {
        var text = "---\ntype: Topic\nsources: [{ id: rev, resource: a.md }]\n---\nA claim.[^Rev]\n\n[^Rev]: Revenue\n";

        Assert.Equal(["7 okf-footnote: footnote [^Rev] matches no sources[].id"], Lines("kb/a.md", text));
    }

    [Fact]
    public void A_footnote_definition_nothing_references_is_not_checked_but_its_links_count()
    {
        var page = Page.Parse("kb/a.md", "---\ntype: Topic\n---\nNo citation.\n\n[^note]: An aside on [a file](b.md)\n", Okf);

        Assert.Empty(page.Findings);
        Assert.Equal(new Link(6, "body", "path", "b.md", "kb/b.md"), Assert.Single(page.Links));
    }

    [Fact]
    public void An_okf_page_has_the_same_body_links_as_any_other()
    {
        var text = "---\ntype: Topic\n---\nA claim.[^1] Another.[^2]\n\n[^1]: https://example.com\n[^2]: [A file](raw/b.md)\n";

        Assert.Equal(Page.Parse("kb/a.md", text, Plain).Links, Page.Parse("kb/a.md", text, Okf).Links);
    }

    [Fact]
    public void A_link_in_a_footnote_is_still_a_body_link()
    {
        var text = "---\ntype: Topic\n---\nA claim.[^1]\n\n[^1]: [A file](raw/b.md)\n";

        var link = Assert.Single(Page.Parse("kb/a.md", text, Okf).Links);

        Assert.Equal(new Link(6, "body", "path", "raw/b.md", "kb/raw/b.md"), link);
    }

    [Theory]
    [InlineData("generated: { by: a/b, at: 2026-06-20T22:53:05 }", "generated.at '2026-06-20T22:53:05' has no UTC offset")]
    [InlineData("generated: { by: a/b, at: 2026-06-20 }", "generated.at '2026-06-20' is not an ISO 8601 datetime with a UTC offset")]
    [InlineData("generated: { by: a/b, at: 2026-06-20 22:53:05Z }", "generated.at '2026-06-20 22:53:05Z' is not an ISO 8601 datetime with a UTC offset")]
    [InlineData("generated: { by: a/b, at: 2026-13-20T22:53:05Z }", "generated.at '2026-13-20T22:53:05Z' is not an ISO 8601 datetime with a UTC offset")]
    [InlineData("generated: { by: a/b, at: [2026] }", "generated.at is not a datetime")]
    [InlineData("verified: { by: human:a, at: 2026-06-20T22:53 }", "verified[].at '2026-06-20T22:53' has no UTC offset")]
    [InlineData("verified: [{ by: human:a, at: 2026-06-20T22:53 }]", "verified[].at '2026-06-20T22:53' has no UTC offset")]
    [InlineData("stale_after: soon", "stale_after 'soon' is not an ISO 8601 datetime with a UTC offset")]
    [InlineData("sources: [{ resource: a.md, last_modified: 2026-06-20T00:00:00 }]", "sources[].last_modified '2026-06-20T00:00:00' has no UTC offset")]
    [InlineData("sources: [{ resource: a.md, usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30 } }]", "sources[].usage_window.to '2026-06-30' is not an ISO 8601 datetime with a UTC offset")]
    [InlineData("usage_window: { from: 2026-06-01T00:00:00, to: 2026-06-30T00:00:00Z }", "usage_window.from '2026-06-01T00:00:00' has no UTC offset")]
    public void A_timestamp_without_a_utc_offset_is_reported(string field, string message)
    {
        Assert.Equal([$"3 okf-timestamp: {message}"], Lines("kb/a.md", $"---\ntype: Metric\n{field}\n---\n"));
    }

    [Theory]
    [InlineData("2026-06-20T22:53:05Z")]
    [InlineData("2026-06-20T22:53Z")]
    [InlineData("2026-06-20T22:53:05.123-07:00")]
    [InlineData("2026-06-20T22:53:05+0530")]
    [InlineData("2026-06-20T22:53:05+05")]
    public void A_timestamp_with_a_utc_offset_is_fine(string timestamp)
    {
        Assert.Empty(Findings("kb/a.md", $"---\ntype: Metric\nstale_after: {timestamp}\n---\n"));
    }

    [Theory]
    [InlineData("generated: { at: 2026-06-20T22:53:05Z }", "3 okf-actor: generated has no by")]
    [InlineData("generated: { by: , at: 2026-06-20T22:53:05Z }", "3 okf-actor: generated has no by")]
    [InlineData("generated: claude", "3 okf-actor: generated has no by")]
    [InlineData("generated: { by: claude, at: 2026-06-20T22:53:05Z }", "3 okf-actor: generated.by 'claude' is not <producer>/<version>, human:<id> or process:<id>")]
    [InlineData("generated: { by: 'human:', at: 2026-06-20T22:53:05Z }", "3 okf-actor: generated.by 'human:' is not <producer>/<version>, human:<id> or process:<id>")]
    [InlineData("generated: { by: team:docs/1, at: 2026-06-20T22:53:05Z }", "3 okf-actor: generated.by 'team:docs/1' is not <producer>/<version>, human:<id> or process:<id>")]
    [InlineData("verified: { by: alister, at: 2026-06-20T22:53:05Z }", "3 okf-actor: verified[].by 'alister' is not <producer>/<version>, human:<id> or process:<id>")]
    [InlineData("verified: [{ by: [human:a], at: 2026-06-20T22:53:05Z }]", "3 okf-actor: verified[].by is not an actor")]
    public void An_actor_outside_the_convention_is_reported(string field, string expected)
    {
        Assert.Equal([expected], Lines("kb/a.md", $"---\ntype: Metric\n{field}\n---\n"));
    }

    [Fact]
    public void A_nested_field_is_reported_on_its_own_line()
    {
        const string text = """
            ---
            type: Metric
            generated:
              by: a/b
              at: 2026-06-20T22:53:05
            verified:
              - by: human:a
                at: 2026-06-25T09:00:00Z
              - by: alister
                at: 2026-06-26T09:00:00
            ---
            """;

        Assert.Equal(
            [
                "5 okf-timestamp: generated.at '2026-06-20T22:53:05' has no UTC offset",
                "10 okf-timestamp: verified[].at '2026-06-26T09:00:00' has no UTC offset",
                "9 okf-actor: verified[].by 'alister' is not <producer>/<version>, human:<id> or process:<id>",
            ],
            Lines("kb/a.md", text + "\n"));
    }

    [Fact]
    public void A_source_author_is_not_checked()
    {
        Assert.Empty(Findings("kb/a.md", "---\ntype: Metric\nsources: [{ resource: a.md, author: team:ga4-docs }]\n---\n"));
    }

    [Theory]
    [InlineData("status: Stable", "status 'Stable' is not draft, stable or deprecated")]
    [InlineData("status: [draft]", "status is not draft, stable or deprecated")]
    public void A_status_outside_the_lifecycle_is_reported(string field, string message)
    {
        Assert.Equal([$"3 okf-status: {message}"], Lines("kb/a.md", $"---\ntype: Metric\n{field}\n---\n"));
    }

    [Fact]
    public void An_empty_status_reads_as_absent()
    {
        Assert.Empty(Findings("kb/a.md", "---\ntype: Metric\nstatus:\n---\n"));
    }

    [Fact]
    public void A_page_markdig_cannot_parse_is_still_checked_from_its_frontmatter()
    {
        var text = "---\ntitle: Deep\n---\n" + new string('>', 200) + " x\n";

        var page = Page.Parse("kb/a.md", text, Okf);

        Assert.NotNull(page.BodyError);
        Assert.Equal(OkfRules.Type, Assert.Single(page.Findings).Rule);
    }
}
