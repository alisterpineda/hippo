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
              "links": {
                "bundles": ["wiki"],
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

    [Fact]
    public async Task Index()
    {
        var json = await Json(0, "index");

        Assert.Equal(5, json.GetProperty("files").GetInt32());
        Assert.True(json.GetProperty("rebuilt").GetBoolean());
    }

    [Fact]
    public async Task Status()
    {
        var json = await Json(0, "status");

        Assert.Equal((5, 4), (json.GetProperty("files").GetProperty("total").GetInt32(), json.GetProperty("files").GetProperty("markdown").GetInt32()));
    }

    [Fact]
    public async Task Files()
    {
        Assert.Equal(
            [".hippo/config.json", "raw/day.md", "raw/lonely.md", "wiki/index.md", "wiki/topics/topic.md"],
            Strings(await Json(0, "files"), "path"));
        Assert.Equal(["wiki/topics/topic.md"], Strings(await Json(0, "files", "--where", "type=Topic"), "path"));
    }

    [Fact]
    public async Task Show()
    {
        var json = await Json(0, "show", "wiki/topics/topic.md");
        var text = await _workspace.RunAsync("show", "wiki/topics/topic.md");

        Assert.Equal("Topic", json.GetProperty("frontmatter").GetProperty("type").GetString());
        Assert.Contains("\"type\": \"Topic\"", text.Stdout);
    }

    [Fact]
    public async Task Refs()
    {
        var json = await Json(0, "refs", "wiki/topics/topic.md");

        Assert.Equal(
            ["4 frontmatter file raw/day.md", "8 body file wiki/index.md", "8 body missing wiki/topics/missing.md"],
            json.EnumerateArray().Select(l =>
                $"{l.GetProperty("line").GetInt64()} {l.GetProperty("kind").GetString()} {l.GetProperty("type").GetString()} {l.GetProperty("target").GetString()}"));
    }

    [Fact]
    public async Task Backrefs()
    {
        var direct = await Json(0, "backrefs", "raw/day.md");
        var transitive = await Json(0, "backrefs", "raw/day.md", "--transitive");

        var link = Assert.Single(direct.EnumerateArray());
        Assert.Equal(("wiki/topics/topic.md", 4, "frontmatter", "../raw/day.md"),
            (link.GetProperty("source").GetString(), link.GetProperty("line").GetInt32(), link.GetProperty("kind").GetString(),
                link.GetProperty("raw").GetString()));
        Assert.Equal(["wiki/index.md", "wiki/topics/topic.md"], Strings(transitive, "source"));
    }

    [Fact]
    public async Task Broken()
    {
        var link = Assert.Single((await Json(1, "broken")).EnumerateArray());

        Assert.Equal(("wiki/topics/topic.md", "missing.md", "wiki/topics/missing.md"),
            (link.GetProperty("source").GetString(), link.GetProperty("raw").GetString(), link.GetProperty("target").GetString()));
    }

    [Fact]
    public async Task Orphans()
    {
        Assert.Equal(["raw/lonely.md"], Strings(await Json(1, "orphans"), "path"));
    }
}
