namespace Hippo;

/// <summary>
/// What a command reads from the process it runs in: the working directory, environment variables and the clock. Tests
/// pass their own to run commands in-process. Two reads stay with the real process: the user cache folder, used when
/// <c>HIPPO_CACHE_DIR</c> is not set, and git, which hippo finds on the real <c>PATH</c> and runs with the real
/// environment.
/// </summary>
internal sealed record CliEnvironment(string WorkingDirectory, Func<string, string?> GetVariable, TimeProvider Clock)
{
    public static CliEnvironment Process() =>
        new(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable, TimeProvider.System);
}
