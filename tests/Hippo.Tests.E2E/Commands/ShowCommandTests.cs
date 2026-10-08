namespace Hippo.Tests.E2E.Commands;

public sealed class ShowCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public ShowCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Show() => _workspace.SmokeAsync(0, "show", "wiki/topics/topic.md");

    [Fact]
    public async Task Show_as_text_prints_the_frontmatter_as_json()
    {
        var text = await _workspace.RunAsync("show", "wiki/topics/topic.md");

        Assert.Contains("\"type\": \"Topic\"", text.Stdout);
    }
}
