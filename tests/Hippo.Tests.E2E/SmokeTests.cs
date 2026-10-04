using System.Text.Json;

namespace Hippo.Tests.E2E;

/// <summary>
/// Runs every command through the binary, with <c>--json</c>, so each query and each JSON shape runs at least once as
/// native AOT compiled it: Dapper.AOT's generated bindings and the source-generated serializers fail only there. What
/// each command answers in detail is tested in-process, in <c>Hippo.Tests.Integration</c>.
/// </summary>
public sealed class SmokeTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public SmokeTests()
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
            type: Topic
            sources:
              - resource: ../raw/day.md
            ---
            # Topic

            Back to [the index](/index.md), and [a gap](missing.md).
            """);
        _workspace.Write("raw/day.md", "# Day\n");
        _workspace.Write("raw/lonely.md", "# Lonely\n");
    }

    public void Dispose() => _workspace.Dispose();

    private async Task<JsonElement> Json(int exitCode, params string[] args)
    {
        var result = await _workspace.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Stderr}");
        Assert.Equal("", result.Stderr);
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static List<string?> Strings(JsonElement array, string property) =>
        array.EnumerateArray().Select(item => item.GetProperty(property).GetString()).ToList();

    [Fact, Covers("index")]
    public async Task Index()
    {
        var json = await Json(0, "index");

        Assert.Equal(4, json.GetProperty("files").GetInt32());
        Assert.True(json.GetProperty("rebuilt").GetBoolean());
    }

    [Fact, Covers("status")]
    public async Task Status()
    {
        var json = await Json(0, "status");

        Assert.Equal((4, 4), (json.GetProperty("files").GetProperty("total").GetInt32(), json.GetProperty("files").GetProperty("markdown").GetInt32()));
    }

    [Fact, Covers("find")]
    public async Task Find()
    {
        var all = await Json(0, "find");

        Assert.Equal(["raw/day.md", "raw/lonely.md", "wiki/index.md", "wiki/topics/topic.md"], Strings(all, "path"));
        Assert.Equal(["Day", "Lonely", "Index", "Topic"], Strings(all, "title"));
        Assert.Equal(["wiki/topics/topic.md"], Strings(await Json(0, "find", "--where", "type=Topic", "--limit", "5"), "path"));

        var fielded = Assert.Single((await Json(0, "find", "--field", "type,sources", "--where", "type", "--where", "type>A")).EnumerateArray());
        var fields = fielded.GetProperty("fields");
        Assert.Equal(("Topic", "../raw/day.md"),
            (fields.GetProperty("type").GetString(), fields.GetProperty("sources")[0].GetProperty("resource").GetString()));
    }

    [Fact, Covers("show")]
    public async Task Show()
    {
        var json = await Json(0, "show", "wiki/topics/topic.md");
        var text = await _workspace.RunAsync("show", "wiki/topics/topic.md");

        Assert.Equal("Topic", json.GetProperty("frontmatter").GetProperty("type").GetString());
        Assert.Contains("\"type\": \"Topic\"", text.Stdout);
    }

    [Fact, Covers("refs")]
    public async Task Refs()
    {
        var json = await Json(0, "refs", "wiki/topics/topic.md");

        Assert.Equal(
            ["4 frontmatter file raw/day.md", "8 body file wiki/index.md", "8 body missing wiki/topics/missing.md"],
            json.EnumerateArray().Select(l =>
                $"{l.GetProperty("line").GetInt64()} {l.GetProperty("kind").GetString()} {l.GetProperty("type").GetString()} {l.GetProperty("target").GetString()}"));
    }

    [Fact, Covers("backrefs")]
    public async Task Backrefs()
    {
        var direct = await Json(0, "backrefs", "raw/day.md");
        var transitive = await Json(0, "backrefs", "raw/day.md", "--transitive");
        var filtered = await Json(0, "backrefs", "raw/day.md", "--transitive", "--from", "wiki/topics/**", "--link-kind", "frontmatter");

        var link = Assert.Single(direct.EnumerateArray());
        Assert.Equal(("wiki/topics/topic.md", 4, "frontmatter", "../raw/day.md"),
            (link.GetProperty("source").GetString(), link.GetProperty("line").GetInt32(), link.GetProperty("kind").GetString(),
                link.GetProperty("raw").GetString()));
        Assert.Equal(["wiki/index.md", "wiki/topics/topic.md"], Strings(transitive, "source"));
        Assert.Equal(["wiki/topics/topic.md"], Strings(filtered, "source"));
    }

    [Fact, Covers("find")]
    public async Task Find_files_with_no_links()
    {
        Assert.Equal(["raw/lonely.md"], Strings(await Json(0, "find", "--no-refs", "--no-backrefs", "--kind", "markdown"), "path"));
        Assert.Equal(["raw/lonely.md"], Strings(await Json(0, "find", "lonely", "--no-refs", "--no-backrefs", "--glob", "!wiki/**"), "path"));
        Assert.Equal(["raw/day.md", "raw/lonely.md"],
            Strings(await Json(0, "find", "--glob", "raw/**", "--no-backrefs", "--from", "wiki/**", "--link-kind", "body"), "path"));
    }

    [Fact, Covers("lint")]
    public async Task Lint()
    {
        _workspace.Write("wiki/index.md", "---\nokf_version: \"0.2\"\n---\n# Index\n\n- [Topic](topics/topic.md)\n");
        _workspace.Write("wiki/draft.md", "---\ntype: Topic\nstatus: final\n---\n");
        _workspace.Write("raw/bad.md", "---\na: [\n---\n");

        var result = await _workspace.RunAsync("lint", "--json");
        var findings = JsonDocument.Parse(result.Stdout).RootElement.EnumerateArray().ToList();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(4, findings.Count);
        Assert.Equal(("frontmatter-syntax", "raw/bad.md", JsonValueKind.Null, 0),
            (findings[0].GetProperty("rule").GetString(), findings[0].GetProperty("path").GetString(), findings[0].GetProperty("line").ValueKind,
                findings[0].GetProperty("related").GetArrayLength()));
        var error = findings[0].GetProperty("message").GetString();
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal($"hippo: warning: cannot read the frontmatter in raw/bad.md: {error}", result.Stderr.Trim());
        Assert.Equal(("okf-index", "wiki/draft.md", JsonValueKind.Null, "wiki/index.md"),
            (findings[1].GetProperty("rule").GetString(), findings[1].GetProperty("path").GetString(), findings[1].GetProperty("line").ValueKind,
                findings[1].GetProperty("related")[0].GetString()));
        Assert.Equal(("okf-status", "wiki/draft.md", 3, 0),
            (findings[2].GetProperty("rule").GetString(), findings[2].GetProperty("path").GetString(), findings[2].GetProperty("line").GetInt32(),
                findings[2].GetProperty("related").GetArrayLength()));
        Assert.Equal(("broken-link", "wiki/topics/topic.md", 8, "missing.md -> wiki/topics/missing.md", "wiki/topics/missing.md"),
            (findings[3].GetProperty("rule").GetString(), findings[3].GetProperty("path").GetString(), findings[3].GetProperty("line").GetInt32(),
                findings[3].GetProperty("message").GetString(), findings[3].GetProperty("related")[0].GetString()));
    }

    [Fact, Covers("find")]
    public async Task Find_with_a_query()
    {
        var porter = await Json(0, "find", "topics", "--where", "type=Topic", "--glob", "wiki/**", "--limit", "5");

        var result = Assert.Single(porter.EnumerateArray());
        Assert.Equal(("wiki/topics/topic.md", "Topic"), (result.GetProperty("path").GetString(), result.GetProperty("title").GetString()));
        Assert.Equal("# Topic Back to [the index](/index.md), and [a gap](missing.md).", result.GetProperty("snippet").GetString());
        // Piped output is not a terminal, so its snippet carries no bold.
        var text = await _workspace.RunAsync("find", "topics", "--where", "type=Topic", "--glob", "wiki/**");
        Assert.Equal("wiki/topics/topic.md  Topic\n  # Topic Back to [the index](/index.md), and [a gap](missing.md).\n",
            text.Stdout.ReplaceLineEndings("\n"));

        _workspace.Write(".hippo/config.json", """
            {
              "bundles": ["wiki"],
              "links": {
                "frontmatter": [{ "field": "sources[].resource", "resolve": "bundle" }]
              },
              "search": { "tokenizer": "trigram" }
            }
            """);
        Assert.Equal(["wiki/topics/topic.md"], Strings(await Json(0, "find", "gap"), "path"));
    }

    [Fact, Covers("cache", "list")]
    public async Task Cache_list()
    {
        var database = (await Json(0, "status")).GetProperty("database").GetString();

        var index = Assert.Single((await Json(0, "cache", "list")).EnumerateArray());

        Assert.Equal(("live", database), (index.GetProperty("state").GetString(), index.GetProperty("database").GetString()));
        Assert.Equal("workspace", Path.GetFileName(index.GetProperty("root").GetString()));
    }

    [Fact, Covers("cache", "prune")]
    public async Task Cache_prune()
    {
        var nested = _workspace.Write("gone/.hippo/config.json", "");
        var gone = Path.GetDirectoryName(Path.GetDirectoryName(nested))!;
        var result = await _workspace.RunInAsync(gone, "index");
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        Directory.Delete(gone, recursive: true);
        await Json(0, "index");

        var removed = Assert.Single((await Json(0, "cache", "prune")).EnumerateArray());

        Assert.Equal(("orphaned", "gone"), (removed.GetProperty("state").GetString(), Path.GetFileName(removed.GetProperty("root").GetString())));
        Assert.False(File.Exists(removed.GetProperty("database").GetString()));
        Assert.Equal("live", Assert.Single((await Json(0, "cache", "list")).EnumerateArray()).GetProperty("state").GetString());
    }
}
