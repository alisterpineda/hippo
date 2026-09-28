namespace Hippo.Tests.Unit;

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
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
