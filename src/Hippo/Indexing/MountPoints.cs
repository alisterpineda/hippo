namespace Hippo.Indexing;

/// <summary>
/// Where filesystems are mounted: <c>/</c>, <c>/mnt/nas</c> and <c>/Volumes/Notes</c> on Unix, drive roots on Windows.
/// An index records the mount point its root is on, so <c>hippo cache</c> can tell an unmounted drive from a deleted
/// workspace: on Unix the folder above a mount point, and often the mount point itself, outlives the unmount.
/// </summary>
internal static class MountPoints
{
    /// <summary>The mount points now, from the OS; none when it cannot list them.</summary>
    public static IReadOnlyList<string> Current()
    {
        try
        {
            return DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The deepest of <paramref name="mountPoints"/> that holds <paramref name="path"/>, or null when none
    /// does, as for a UNC path on Windows.</summary>
    public static string? Containing(string path, IEnumerable<string> mountPoints) =>
        mountPoints.Where(mount => Holds(mount, path)).MaxBy(mount => mount.Length);

    /// <summary>Whether <paramref name="mountPoint"/> is in <paramref name="mountPoints"/>.</summary>
    public static bool IsMounted(string mountPoint, IEnumerable<string> mountPoints) =>
        mountPoints.Any(mount => Trim(mount).Equals(Trim(mountPoint), Comparison));

    private static bool Holds(string mountPoint, string path)
    {
        var mount = Trim(mountPoint);
        var target = Trim(path);
        if (target.Equals(mount, Comparison))
        {
            return true;
        }
        // A root such as / or C:\ keeps its separator when trimmed; any other mount point needs one after it, so
        // /mnt/nas does not hold /mnt/nas2.
        var prefix = Path.EndsInDirectorySeparator(mount) ? mount : mount + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, Comparison);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
