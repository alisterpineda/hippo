namespace Hippo.Tests.E2E.Commands;

public sealed class InitCommandTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>init has no <c>--json</c>, so its smoke test checks only the exit code and stderr.</summary>
    [Fact, Trait(Traits.Category, Traits.Smoke)]
    public async Task Init()
    {
        Directory.Delete(_workspace.Combine(".hippo"), recursive: true);

        var init = await _workspace.RunAsync("init");

        Assert.Equal(0, init.ExitCode);
        Assert.Equal("", init.Stderr);
    }
}
