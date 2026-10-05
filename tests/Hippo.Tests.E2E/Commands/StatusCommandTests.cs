using System.Text.Json;

namespace Hippo.Tests.E2E.Commands;

public sealed class StatusCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public StatusCommandTests() => _workspace.WriteSample();

    public void Dispose() => _workspace.Dispose();

    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public Task Status() => _workspace.SmokeAsync(0, JsonValueKind.Object, "status");
}
