using System.Text.Json;

namespace Hippo.Tests.E2E.Commands;

public sealed class BackrefsCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public BackrefsCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    private static List<string?> Strings(JsonElement array, string property) =>
        array.EnumerateArray().Select(item => item.GetProperty(property).GetString()).ToList();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Backrefs() => _workspace.SmokeAsync(0, JsonValueKind.Array, "backrefs", "raw/day.md");

    [Fact]
    public async Task Transitive_backrefs_filtered_by_source_and_link_kind()
    {
        var transitive = await _workspace.JsonAsync(0, "backrefs", "raw/day.md", "--transitive");
        var filtered = await _workspace.JsonAsync(0, "backrefs", "raw/day.md", "--transitive", "--from", "wiki/topics/**", "--link-kind", "frontmatter");

        Assert.Equal(["wiki/index.md", "wiki/topics/topic.md"], Strings(transitive, "source"));
        Assert.Equal(["wiki/topics/topic.md"], Strings(filtered, "source"));
    }
}
