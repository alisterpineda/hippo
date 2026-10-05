namespace Hippo.Tests.E2E;

/// <summary>
/// The traits E2E tests carry, as <c>[Trait(Traits.Category, Traits.Smoke)]</c>, so a run can pick them out with
/// <c>dotnet test --filter "Category=Smoke"</c>.
/// </summary>
public static class Traits
{
    public const string Category = "Category";

    /// <summary>The one test per leaf command that checks it runs under the binary: its exit code, nothing on stderr,
    /// and, with <c>--json</c>, a non-empty array or object on stdout. <see cref="CommandCoverageTests"/> checks that
    /// each leaf command's class has exactly one.</summary>
    public const string Smoke = "Smoke";

    public const string Topic = "Topic";

    /// <summary>The files git ignores, which hippo asks a real git for.</summary>
    public const string Gitignore = "Gitignore";
}
