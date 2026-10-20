namespace Hippo.Commands.Self;

/// <summary>
/// Whether this hippo is a .NET tool, told by the <c>.store</c> folder the CLI installs tools under, and what to run
/// to update one. Only the refusal message reads this: a tool has no receipt, so <c>self update</c> refuses it either
/// way, and this chooses the advice.
/// </summary>
internal static class ToolStore
{
    /// <summary>The folder a .NET tool was installed to, the one above the <c>.store</c> segment of
    /// <paramref name="baseDirectory"/>; null when there is no such segment.</summary>
    public static string? InstallRoot(string baseDirectory)
    {
        for (var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory))); folder is not null; folder = folder.Parent)
        {
            if (folder.Name == ".store" && folder.Parent is { } root)
            {
                return root.FullName;
            }
        }
        return null;
    }

    /// <summary>The command that updates the tool at <paramref name="installRoot"/>: the global form when that is the
    /// global tools folder, else one naming the folder, since <c>-g</c> on a <c>--tool-path</c> install would make a
    /// second, global copy.</summary>
    public static string UpdateCommand(string installRoot, Func<string, string?> getVariable) =>
        IsGlobal(installRoot, getVariable) ? "dotnet tool update -g hippo" : $"dotnet tool update hippo --tool-path {installRoot}";

    /// <summary>Whether <paramref name="installRoot"/> is the global tools folder, <c>.dotnet/tools</c> under
    /// <c>DOTNET_CLI_HOME</c> or else under the user's home, as the CLI finds it.</summary>
    private static bool IsGlobal(string installRoot, Func<string, string?> getVariable)
    {
        var home = getVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = getVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        }
        if (string.IsNullOrEmpty(home))
        {
            return false;
        }
        var global = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(home, ".dotnet", "tools")));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), global, comparison);
    }
}
