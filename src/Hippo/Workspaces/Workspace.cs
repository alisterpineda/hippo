using System.IO.Enumeration;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hippo.Workspaces;

/// <summary>A file in the workspace. <see cref="Path"/> is its key.</summary>
internal sealed record WorkspaceFile(string Path, string FullPath, long Size, DateTimeOffset Modified);

/// <summary>A workspace: the folder holding <c>.hippo/config.json</c>, and the conventions that file declares.</summary>
internal sealed record Workspace(string Root, WorkspaceConfig Config)
{
    /// <summary>Finds the workspace by walking up from <paramref name="workingDirectory"/> to the first folder that has
    /// a <c>.hippo/config.json</c>, and loads that config.</summary>
    public static Workspace Open(string workingDirectory)
    {
        var root = FindRoot(workingDirectory)
            ?? throw new HippoException($"no {WorkspaceConfig.RelativePath} in {Start(workingDirectory)} or any folder above it; run hippo init at the workspace root");
        var path = WorkspaceConfig.PathIn(root);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HippoException($"cannot read {path}: {ex.Message}");
        }

        return new Workspace(root, WorkspaceConfig.Parse(json));
    }

    /// <summary>A file whose name ends in <c>.md</c> is <c>markdown</c>; every other file is <c>other</c>.</summary>
    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.Ordinal);

    /// <summary>
    /// Turns a path relative to the root, in the OS's form, into the key the index stores: <c>/</c> separators on
    /// every OS. Only Windows separates with <c>\</c>; elsewhere <c>\</c> is an ordinary file-name character and stays.
    /// </summary>
    public static string Key(string relativePath) =>
        Path.DirectorySeparatorChar == '\\' ? relativePath.Replace('\\', '/') : relativePath;

    /// <summary>Resolves <paramref name="path"/> against <paramref name="workingDirectory"/> and returns its key, or
    /// throws when it is not inside the workspace. The root itself is <c>""</c> when <paramref name="allowRoot"/> is set,
    /// and otherwise an error, since it is not a file.</summary>
    public string KeyOf(string path, string workingDirectory, bool allowRoot = false)
    {
        var relative = Path.GetRelativePath(Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, workingDirectory)));
        if (relative == "." && allowRoot)
        {
            return "";
        }
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new HippoException($"{path} is not a file inside the workspace at {Root}");
        }
        return Key(relative);
    }

    /// <summary>The full path of the file stored under <paramref name="key"/>.</summary>
    public string FullPath(string key) => Path.Combine(Root, key);

    /// <summary>The keys of the files in <paramref name="fullPaths"/> that <paramref name="matcher"/> matches,
    /// with its patterns taken relative to the root.</summary>
    public HashSet<string> Match(Matcher matcher, IEnumerable<string> fullPaths) =>
        matcher.Match(Root, fullPaths).Files.Select(match => Key(match.Path)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The keys among <paramref name="keys"/> that any of the globs <paramref name="patterns"/>, taken relative
    /// to the root, matches.</summary>
    public HashSet<string> Glob(IEnumerable<string> patterns, IEnumerable<string> keys)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(patterns);
        return Match(matcher, keys.Select(FullPath));
    }

    /// <summary>Lists the included files under the root, less those git ignores when the config says to; git's
    /// complaints go to <paramref name="warnings"/>. Symbolic links are skipped, so nothing outside the root is read, and
    /// a folder that an exclude pattern ending in <c>/**</c> covers, or whose files git all ignores, is never entered.
    /// Nor is the root's own <see cref="WorkspaceConfig.Folder"/>, whatever the config includes: it is hippo's, not the
    /// workspace's. A nested workspace's is an ordinary folder.</summary>
    public List<WorkspaceFile> ListFiles(List<string> warnings)
    {
        var ignored = Config.Gitignore ? GitIgnored.Find(Root, warnings) : GitIgnored.None;
        var pruned = Config.Exclude
            .Where(pattern => pattern.EndsWith("/**", StringComparison.Ordinal))
            .Select(pattern =>
            {
                var folder = new Matcher(StringComparison.Ordinal);
                folder.AddInclude(pattern[..^3]);
                return folder;
            })
            .ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
        };
        var files = new FileSystemEnumerable<WorkspaceFile>(
            Root,
            (ref entry) => new WorkspaceFile(RelativeKey(ref entry), entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc),
            options)
        {
            ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && !IsLink(ref entry),
            ShouldRecursePredicate = (ref entry) =>
            {
                if (IsLink(ref entry))
                {
                    return false;
                }
                var path = RelativeKey(ref entry);
                // Case is ignored because a filesystem that ignores it finds the root through a .Hippo too.
                return !string.Equals(path, WorkspaceConfig.Folder, StringComparison.OrdinalIgnoreCase)
                    && !ignored.Folders.Contains(path) && !pruned.Exists(folder => folder.Match(Root, path).HasMatches);
            },
        }.ToList();

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(Config.Include);
        matcher.AddExcludePatterns(Config.Exclude);
        var included = Match(matcher, files.Select(f => f.FullPath));
        return files.Where(f => included.Contains(f.Path) && !ignored.Files.Contains(f.Path)).ToList();
    }

    private static bool IsLink(ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string RelativeKey(ref FileSystemEntry entry)
    {
        var directory = entry.Directory[entry.RootDirectory.Length..].TrimStart(['/', '\\']);
        var path = directory.IsEmpty ? entry.FileName.ToString() : $"{directory}/{entry.FileName}";
        return Key(path);
    }

    /// <summary>The nearest folder at or above <paramref name="workingDirectory"/> that has a <c>.hippo/config.json</c>,
    /// or null when none does.</summary>
    public static string? FindRoot(string workingDirectory)
    {
        for (var dir = new DirectoryInfo(Start(workingDirectory)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(WorkspaceConfig.PathIn(dir.FullName)))
            {
                return Path.TrimEndingDirectorySeparator(dir.FullName);
            }
        }
        return null;
    }

    private static string Start(string workingDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
}
