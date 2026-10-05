using System.Text.Json;

namespace Hippo.Tests.E2E.Commands;

public sealed class LintCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public LintCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    private const string Declaration = "---\nokf_version: \"0.2\"\n---\n";

    /// <summary>The sample has a broken link, and lint exits 1 when it finds anything. Its bundle declares an OKF
    /// version, as lint warns of one that does not.</summary>
    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Lint()
    {
        _workspace.Write("wiki/index.md", Declaration + TempWorkspace.SampleIndex);

        return _workspace.SmokeAsync(1, JsonValueKind.Array, "lint");
    }

    [Fact]
    public async Task Findings_of_the_frontmatter_okf_and_broken_link_rules()
    {
        _workspace.Write("wiki/index.md", Declaration + TempWorkspace.SampleIndex);
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
}
