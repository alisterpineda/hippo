using System.Text.Json;

namespace Hippo.Tests.E2E;

/// <summary>
/// A <see cref="WorkspaceFixture"/> where hippo runs as a process, with the workspace as its working directory and
/// <c>HIPPO_CACHE_DIR</c> pointing at the cache, so no test touches the user's cache. hippo, and git through it, get
/// the same isolation as <see cref="WorkspaceFixture.Git"/>: no global or system git config, and none of the test
/// runner's <c>GIT_</c> variables.
/// </summary>
public sealed class TempWorkspace : WorkspaceFixture
{
    public Task<HippoProcess.Result> RunAsync(params string[] args) => RunInAsync(Root, args);

    public Task<HippoProcess.Result> RunInAsync(string workingDirectory, params string[] args) =>
        HippoProcess.RunAsync(workingDirectory, new Dictionary<string, string>(GitEnvironment) { ["HIPPO_CACHE_DIR"] = CacheDir }, args);

    /// <summary>Runs hippo in the workspace with <paramref name="environment"/> in place of the cache override, over the
    /// git isolation.</summary>
    public Task<HippoProcess.Result> RunWithAsync(IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var merged = GitEnvironment;
        foreach (var (name, value) in environment)
        {
            merged[name] = value;
        }
        return HippoProcess.RunAsync(Root, merged, args);
    }

    /// <summary>Runs hippo with <c>--json</c>, checks the exit code and that stderr is empty, and returns stdout parsed.</summary>
    public async Task<JsonElement> JsonAsync(int exitCode, params string[] args)
    {
        var result = await RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == exitCode, $"exit {result.ExitCode}: {result.Stderr}");
        Assert.Equal("", result.Stderr);
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    /// <summary>What a smoke test checks: hippo, run with <c>--json</c>, exits with <paramref name="exitCode"/>, writes
    /// nothing to stderr, and writes a non-empty JSON object to stdout. A command that lists things uses
    /// <see cref="SmokeListAsync"/>.</summary>
    public async Task SmokeAsync(int exitCode, params string[] args)
    {
        var json = await JsonAsync(exitCode, args);

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.True(json.EnumerateObject().Any(), $"empty JSON: {json}");
    }

    /// <summary><see cref="SmokeAsync"/> for a command that lists things: the list under <paramref name="list"/> must be
    /// non-empty, since an object holding an empty list is not empty itself.</summary>
    public async Task SmokeListAsync(int exitCode, string list, params string[] args)
    {
        var json = await JsonAsync(exitCode, args);

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.True(json.GetProperty(list).GetArrayLength() > 0, $"empty JSON: {json}");
    }

    /// <summary>The sample's <c>wiki/index.md</c>, for a test that rewrites it with something added.</summary>
    public const string SampleIndex = "# Index\n\n- [Topic](topics/topic.md)\n";

    /// <summary>
    /// Writes a small workspace the command tests share: a bundle <c>wiki</c>, declaring no OKF version, whose
    /// <c>index.md</c> links to <c>topics/topic.md</c>, which has <c>type: Topic</c>, a frontmatter link to
    /// <c>raw/day.md</c>, a body link back to the index, and a broken body link to <c>missing.md</c>; and
    /// <c>raw/lonely.md</c>, with no links either way.
    /// </summary>
    public void WriteSample()
    {
        Write(".hippo/config.json", """
            {
              "bundles": ["wiki"],
              "links": {
                "frontmatter": [{ "field": "sources[].resource", "resolve": "bundle" }]
              }
            }
            """);
        Write("wiki/index.md", SampleIndex);
        Write("wiki/topics/topic.md", """
            ---
            type: Topic
            sources:
              - resource: ../raw/day.md
            ---
            # Topic

            Back to [the index](/index.md), and [a gap](missing.md).
            """);
        Write("raw/day.md", "# Day\n");
        Write("raw/lonely.md", "# Lonely\n");
    }
}
