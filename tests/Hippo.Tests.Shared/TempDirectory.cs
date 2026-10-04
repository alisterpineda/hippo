namespace Hippo.Tests;

/// <summary>A fresh directory under the system temp folder, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string FullPath { get; } = Path.Combine(Path.GetTempPath(), "hippo-tests", Guid.NewGuid().ToString("n"));

    public TempDirectory() => Directory.CreateDirectory(FullPath);

    public string Combine(string relativePath) => Path.Combine(FullPath, relativePath);

    public string Write(string relativePath, string content)
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            // git writes its objects read-only, and Windows will not delete a read-only file.
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(FullPath, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
            }
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
