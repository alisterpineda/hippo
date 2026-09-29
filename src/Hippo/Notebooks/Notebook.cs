using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Notebooks;

/// <summary>A notebook: the folder holding <c>.hippo/config.json</c>, and the conventions that file declares.</summary>
internal sealed record Notebook(string Root, NotebookConfig Config)
{
    /// <summary>Finds the notebook by walking up from <paramref name="workingDirectory"/> to the first folder that has
    /// a <c>.hippo/config.json</c>, and loads that config.</summary>
    public static Notebook Open(string workingDirectory)
    {
        var root = FindRoot(workingDirectory)
            ?? throw new HippoException($"no {NotebookConfig.RelativePath} in {Start(workingDirectory)} or any folder above it; run hippo init at the notebook root");
        var path = NotebookConfig.PathIn(root);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HippoException($"cannot read {path}: {ex.Message}");
        }

        return new Notebook(root, NotebookConfig.Parse(json));
    }

    /// <summary>A file whose name ends in <c>.md</c> is markdown; every other file is plain.</summary>
    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.Ordinal);

    /// <summary>
    /// Turns a path relative to the root, in the OS's form, into the key the index stores: <c>/</c> separators on
    /// every OS. Only Windows separates with <c>\</c>; elsewhere <c>\</c> is an ordinary file-name character and stays.
    /// </summary>
    public static string Key(string relativePath) =>
        Path.DirectorySeparatorChar == '\\' ? relativePath.Replace('\\', '/') : relativePath;

    /// <summary>Resolves <paramref name="path"/> against the working directory and returns its key, or throws when
    /// it is not inside the notebook.</summary>
    public string KeyOf(string path)
    {
        var relative = Path.GetRelativePath(Root, Path.GetFullPath(path));
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new HippoException($"{path} is not a file inside the notebook at {Root}");
        }
        return Key(relative);
    }

    /// <summary>The full path of the file stored under <paramref name="key"/>.</summary>
    public string FullPath(string key) => Path.Combine(Root, key);

    /// <summary>The keys of the files in <paramref name="fullPaths"/> that <paramref name="matcher"/> matches,
    /// with its patterns taken relative to the root.</summary>
    public HashSet<string> Match(Matcher matcher, IEnumerable<string> fullPaths) =>
        matcher.Match(Root, fullPaths).Files.Select(match => Key(match.Path)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The nearest folder at or above <paramref name="workingDirectory"/> that has a <c>.hippo/config.json</c>,
    /// or null when none does.</summary>
    public static string? FindRoot(string workingDirectory)
    {
        for (var dir = new DirectoryInfo(Start(workingDirectory)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(NotebookConfig.PathIn(dir.FullName)))
            {
                return Path.TrimEndingDirectorySeparator(dir.FullName);
            }
        }
        return null;
    }

    private static string Start(string workingDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
}
