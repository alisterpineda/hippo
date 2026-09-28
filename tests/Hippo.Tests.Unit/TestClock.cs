namespace Hippo.Tests.Unit;

/// <summary>A clock that reads whatever time the test sets, then moves on by <see cref="Step"/> after each read.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public TimeSpan Step { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        var now = Now;
        Now += Step;
        return now;
    }
}
