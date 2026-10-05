using System.Text.Json;

namespace Hippo.Tests.E2E.Commands;

public sealed class CachePruneCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>Indexes a workspace and deletes it, so prune has an orphan to remove and lists it.</summary>
    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public async Task Cache_prune()
    {
        var nested = _workspace.Write("gone/.hippo/config.json", "");
        var gone = Path.GetDirectoryName(Path.GetDirectoryName(nested))!;
        var result = await _workspace.RunInAsync(gone, "index");
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        Directory.Delete(gone, recursive: true);

        await _workspace.SmokeAsync(0, JsonValueKind.Array, "cache", "prune");
    }
}
