namespace Hippo.Tests.E2E.Commands;

public sealed class CacheListCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public CacheListCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public async Task Cache_list()
    {
        // The cache is empty until a command indexes the workspace.
        await _workspace.JsonAsync(0, "index");

        await _workspace.SmokeListAsync(0, "indexes", "cache", "list");
    }
}
