using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Hippo.Indexing;

/// <summary>
/// The one name of a workspace root that its index is keyed by, so every way of reaching a folder finds the same index.
/// On Windows that is the final path the OS reports for the folder: stored letter case, long names for 8.3 short names,
/// and junctions, symlinks, <c>subst</c> drives and mapped drives resolved. On Unix the path is kept as given, since
/// <c>getcwd</c> already returns the physical path. A path the OS cannot resolve is kept as given, as if never resolved;
/// the cost is a second index, never a shared one, because two folders never have one final path.
/// </summary>
internal static partial class CanonicalPath
{
    public static string Of(string path) => OperatingSystem.IsWindows() ? FinalPath(path) ?? path : path;

    private const uint OpenExisting = 3;

    /// <summary>Needed to open a folder rather than a file.</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary><c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>: a drive-letter or UNC path, links resolved.</summary>
    private const uint FinalPathFlags = 0;

    [SupportedOSPlatform("windows")]
    private static string? FinalPath(string path)
    {
        // No access rights are asked for: querying the name needs none, and so the folder's permissions cannot refuse it.
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[260];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FinalPathFlags);
            if (length == 0)
            {
                return null;
            }
            if (length < buffer.Length)
            {
                return Path.TrimEndingDirectorySeparator(WithoutPrefix(new string(buffer, 0, (int)length)));
            }
            // Too small: length is the size needed, terminator included.
            buffer = new char[length];
        }
    }

    /// <summary>Drops the <c>\\?\</c> prefix the OS puts on a final path: <c>\\?\C:\x</c> is <c>C:\x</c>, and
    /// <c>\\?\UNC\server\share</c> is <c>\\server\share</c>.</summary>
    internal static string WithoutPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[@"\\?\UNC\".Length..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[@"\\?\".Length..]
        : path;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, nint securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] filePath,
        uint filePathSize, uint flags);
}
