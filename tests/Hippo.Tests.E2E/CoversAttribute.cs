namespace Hippo.Tests.E2E;

/// <summary>Marks a test as the smoke test for this command: it runs the command through the binary, with
/// <c>--json</c> when the command has it, so its queries and JSON shape run as native AOT compiled them (see
/// <see cref="SmokeTests"/>). A test merely invoking the command does not qualify. A test in the E2E project checks
/// that every leaf command in <c>hippo --help</c> has at least one, and that none claims a command that is gone.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CoversAttribute(params string[] command) : Attribute
{
    public string[] Command { get; } = command;
}
