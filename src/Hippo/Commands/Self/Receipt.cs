using System.Text.Json;

namespace Hippo.Commands.Self;

/// <summary>What the receipt beside the binary says.</summary>
internal enum ReceiptState
{
    /// <summary>An install script wrote it, so <c>self update</c> may run the script for this directory.</summary>
    Found,

    /// <summary>There is none: a .NET tool, a copy made by hand, a dev build, or an install from before the scripts wrote one.</summary>
    Missing,

    /// <summary>The file is there but is not one JSON object.</summary>
    Unreadable,

    /// <summary>It parses, but <c>installedBy</c> is not an install script: a receipt from an installer this hippo
    /// does not know, which refuses as a missing one does.</summary>
    Unknown,
}

/// <summary>
/// The receipt the install scripts leave beside the binary, <c>.hippo-receipt.json</c>, saying that one of them put
/// hippo there. Its name and content are the receipt contract in <c>docs/dev/releasing.md</c>: one JSON object
/// with <c>installedBy</c>, which is all hippo reads, and fields any version may add, which hippo ignores. It says who
/// manages the binary, not whether to trust it: forging one takes write access to the directory, which already means
/// owning the binary.
/// </summary>
internal static class Receipt
{
    public const string FileName = ".hippo-receipt.json";

    private static readonly string[] Scripts = ["install.sh", "install.ps1"];

    /// <summary>Reads the receipt in <paramref name="directory"/>.</summary>
    public static ReceiptState Read(string directory)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(directory, FileName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReceiptState.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReceiptState.Unreadable;
        }

        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ReceiptState.Unreadable;
            }
            return json.RootElement.TryGetProperty("installedBy", out var installedBy)
                && installedBy.ValueKind == JsonValueKind.String
                && Scripts.Contains(installedBy.GetString(), StringComparer.Ordinal)
                ? ReceiptState.Found
                : ReceiptState.Unknown;
        }
        catch (JsonException)
        {
            return ReceiptState.Unreadable;
        }
    }
}
