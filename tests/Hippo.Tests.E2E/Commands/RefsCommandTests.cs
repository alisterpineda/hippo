namespace Hippo.Tests.E2E.Commands;

public sealed class RefsCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public RefsCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Refs() => _workspace.SmokeListAsync(0, "links", "refs", "wiki/topics/topic.md");
}
