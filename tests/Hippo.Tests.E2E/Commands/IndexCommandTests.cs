namespace Hippo.Tests.E2E.Commands;

public sealed class IndexCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public IndexCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Index() => _workspace.SmokeAsync(0, "index");
}
