using System.Text.Json;

namespace Hippo.Tests.E2E.Commands;

public sealed class BackrefsCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public BackrefsCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    /// <summary>The path of each file <c>backrefs --transitive --json</c> printed.</summary>
    private static List<string?> Paths(JsonElement json) =>
        json.GetProperty("files").EnumerateArray().Select(item => item.GetProperty("path").GetString()).ToList();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Backrefs() => _workspace.SmokeListAsync(0, "links", "backrefs", "raw/day.md");

    [Fact]
    public async Task Transitive_backrefs_filtered_by_source_and_link_kind()
    {
        var transitive = await _workspace.JsonAsync(0, "backrefs", "raw/day.md", "--transitive");
        var filtered = await _workspace.JsonAsync(0, "backrefs", "raw/day.md", "--transitive", "--from", "wiki/topics/**", "--link-kind", "frontmatter");

        Assert.Equal(["wiki/index.md", "wiki/topics/topic.md"], Paths(transitive));
        Assert.Equal(["wiki/topics/topic.md"], Paths(filtered));
    }
}
