using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hippo.Cache;
using Hippo.Commands;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Integration;

public sealed class CacheCommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == ExitCode.Clean, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static List<(string? Root, string State)> Entries(JsonElement json) =>
        json.EnumerateArray().Select(i => (i.GetProperty("root").GetString(), i.GetProperty("state").GetString()!)).ToList();

    /// <summary><paramref name="entries"/> in the order <c>cache list</c> prints them: by root, no root last.</summary>
    private static List<(string? Root, string State)> InListOrder(params (string? Root, string State)[] entries) =>
        entries.OrderBy(e => e.Root is null).ThenBy(e => e.Root, StringComparer.Ordinal).ToList();

    private List<(string? Root, string State)> List() => Entries(Json(_workspace.Run("cache", "list", "--json")));

    /// <summary>Runs a command in the workspace at <paramref name="root"/>, so the cache holds its index, and returns
    /// the root as the index records it.</summary>
    private string Index(string root)
    {
        var result = _workspace.RunIn(root, "index");
        Assert.True(result.ExitCode == ExitCode.Clean, $"exit {result.ExitCode}: {result.Stderr}");
        return CanonicalPath.Of(root);
    }

    private string DatabaseOf(string workingDirectory) =>
        Json(_workspace.RunIn(workingDirectory, "status", "--json")).GetProperty("database").GetString()!;

    /// <summary>A workspace that was indexed and then deleted.</summary>
    private string Orphan(string relativePath)
    {
        var root = Index(_workspace.AddWorkspace(relativePath));
        Directory.Delete(_workspace.Beside(relativePath), recursive: true);
        return root;
    }

    /// <summary>A workspace that was indexed, then deleted with the folder above it.</summary>
    private string Unreachable()
    {
        var root = Index(_workspace.AddWorkspace(Path.Combine("drive", "notes")));
        Directory.Delete(_workspace.Beside("drive"), recursive: true);
        return root;
    }

    /// <summary>An index from before roots were recorded.</summary>
    private string Unrecorded()
    {
        var database = Path.Combine(_workspace.CacheDir, new string('a', 64), CacheLocation.DatabaseName);
        IndexDatabase.Open(database).Dispose();
        return database;
    }

    [Fact]
    public void Every_command_records_its_workspace_root()
    {
        var database = DatabaseOf(_workspace.Root);

        var index = Assert.Single(Json(_workspace.Run("cache", "list", "--json")).EnumerateArray());
        Assert.Equal(CanonicalPath.Of(_workspace.Root), index.GetProperty("root").GetString());
        Assert.Equal("live", index.GetProperty("state").GetString());
        Assert.Equal(database, index.GetProperty("database").GetString());
        Assert.True(index.GetProperty("size").GetInt64() > 0);
    }

    [Fact]
    public void Two_copies_of_a_workspace_get_separate_indexes()
    {
        _workspace.Write("a.md", "# A\n");
        Index(_workspace.Root);
        var copy = _workspace.Beside("copy");
        CopyFolder(_workspace.Root, copy);
        File.WriteAllText(Path.Combine(copy, "b.md"), "# B\n");

        var original = Json(_workspace.Run("find", "--json")).EnumerateArray().Select(f => f.GetProperty("path").GetString());
        var copied = Json(_workspace.RunIn(copy, "find", "--json")).EnumerateArray().Select(f => f.GetProperty("path").GetString());

        Assert.Equal(["a.md"], original);
        Assert.Equal(["a.md", "b.md"], copied);
        Assert.NotEqual(DatabaseOf(_workspace.Root), DatabaseOf(copy));
        Assert.Equal(2, List().Count(i => i.State == "live"));
    }

    [Fact]
    public void Differently_cased_working_directories_share_one_index()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths are case-insensitive, and hippo resolves the stored case");

        Assert.Equal(DatabaseOf(_workspace.Root), DatabaseOf(_workspace.Root.ToUpperInvariant()));
        Assert.Equal(DatabaseOf(_workspace.Root), DatabaseOf(_workspace.Root.ToLowerInvariant()));
        Assert.Single(List());
    }

    [Fact]
    public void An_index_whose_workspace_was_deleted_is_orphaned()
    {
        var root = Orphan("gone");

        Assert.Equal([(root, "orphaned")], List());
    }

    [Fact]
    public void An_index_whose_root_lost_its_config_is_orphaned()
    {
        var root = Index(_workspace.AddWorkspace("unmade"));
        Directory.Delete(Path.Combine(_workspace.Beside("unmade"), ".hippo"), recursive: true);

        Assert.Equal([(root, "orphaned")], List());
    }

    [Fact]
    public void An_index_whose_root_and_the_folder_above_are_gone_is_unreachable()
    {
        var root = Unreachable();

        Assert.Equal([(root, "unreachable")], List());
    }

    /// <summary>A folder beside the workspace that the test treats as a mounted drive, from now until it is unmounted
    /// by leaving it out of <see cref="TestWorkspace.MountPoints"/>. Named by its canonical path, as the root recorded
    /// on it is.</summary>
    private string MountDrive()
    {
        var drive = _workspace.Beside("drive");
        Directory.CreateDirectory(drive);
        drive = CanonicalPath.Of(drive);
        _workspace.MountPoints = [.. _workspace.MountPoints, drive];
        return drive;
    }

    private void Unmount(string drive) => _workspace.MountPoints = _workspace.MountPoints.Where(m => m != drive).ToList();

    private string? RecordedVolume(string root)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(root), CacheLocation.DatabaseName),
            Pooling = false,
        }.ToString());
        db.Open();
        return IndexMeta.Get(db, IndexMeta.Volume);
    }

    [Fact]
    public void An_index_records_the_mount_point_its_root_is_on()
    {
        var drive = MountDrive();

        var onDrive = Index(_workspace.AddWorkspace(Path.Combine("drive", "notes")));
        var elsewhere = Index(_workspace.Root);

        Assert.Equal(drive, RecordedVolume(onDrive));
        Assert.Equal(Path.GetPathRoot(Path.GetTempPath()), RecordedVolume(elsewhere));
    }

    [Fact]
    public void A_workspace_on_an_unmounted_drive_is_unreachable_and_kept()
    {
        var drive = MountDrive();
        var root = Index(_workspace.AddWorkspace(Path.Combine("drive", "notes")));
        // Unmounted, the drive's files are gone but the folder it was mounted on stays, as it does on Linux.
        Directory.Delete(_workspace.Beside(Path.Combine("drive", "notes")), recursive: true);
        Unmount(drive);

        Assert.Equal([(root, "unreachable")], List());
        Assert.Empty(Json(_workspace.Run("cache", "prune", "--json")).EnumerateArray());
        Assert.Equal([(root, "unreachable")], List());
    }

    [Fact]
    public void A_workspace_at_a_mount_point_is_unreachable_while_unmounted_and_orphaned_once_mounted()
    {
        var drive = MountDrive();
        _workspace.AddWorkspace("drive");
        var root = Index(_workspace.Beside("drive"));
        // An empty mount point, as the folder of an unmounted drive, or one whose workspace was since deleted.
        Directory.Delete(_workspace.Beside(Path.Combine("drive", ".hippo")), recursive: true);
        Unmount(drive);

        Assert.Equal([(root, "unreachable")], List());
        _workspace.MountPoints = [.. _workspace.MountPoints, drive];
        Assert.Equal([(root, "orphaned")], List());
    }

    [Fact]
    public void A_deleted_workspace_on_a_mounted_drive_is_orphaned()
    {
        MountDrive();
        var root = Index(_workspace.AddWorkspace(Path.Combine("drive", "notes")));
        Directory.Delete(_workspace.Beside(Path.Combine("drive", "notes")), recursive: true);

        Assert.Equal([(root, "orphaned")], List());
    }

    [Fact]
    public void A_workspace_on_no_known_mount_point_is_judged_by_its_root_alone()
    {
        _workspace.MountPoints = [];
        var root = Orphan("gone");

        Assert.Equal("", RecordedVolume(root));
        Assert.Equal([(root, "orphaned")], List());
    }

    [Fact]
    public void An_index_without_a_recorded_mount_point_gets_one_on_the_next_command()
    {
        var root = Index(_workspace.Root);
        using (var db = IndexDatabase.Open(Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(root), CacheLocation.DatabaseName)))
        {
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM meta WHERE key = 'volume'";
            command.ExecuteNonQuery();
        }
        Assert.Null(RecordedVolume(root));

        Index(_workspace.Root);

        Assert.Equal(Path.GetPathRoot(Path.GetTempPath()), RecordedVolume(root));
    }

    [Fact]
    public void An_index_that_records_no_root_or_cannot_be_read_is_unknown()
    {
        var unrecorded = Unrecorded();
        var junk = Path.Combine(_workspace.CacheDir, new string('b', 64), CacheLocation.DatabaseName);
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllText(junk, "not a database");
        var empty = Path.Combine(_workspace.CacheDir, new string('c', 64));
        Directory.CreateDirectory(empty);

        var json = Json(_workspace.Run("cache", "list", "--json"));

        Assert.All(Entries(json), i => Assert.Equal((null, "unknown"), i));
        Assert.Equal(
            [unrecorded, junk, Path.Combine(empty, CacheLocation.DatabaseName)],
            json.EnumerateArray().Select(i => i.GetProperty("database").GetString()));
    }

    [Fact]
    public void Folders_not_named_as_indexes_are_left_out()
    {
        Directory.CreateDirectory(Path.Combine(_workspace.CacheDir, "notes"));
        Directory.CreateDirectory(Path.Combine(_workspace.CacheDir, new string('A', 64)));

        Assert.Empty(List());
    }

    [Fact]
    public void A_missing_cache_folder_lists_nothing()
    {
        var result = _workspace.RunWith(
            new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = _workspace.Beside("nowhere") }, "cache", "list", "--json");

        Assert.Empty(Json(result).EnumerateArray());
        Assert.False(Directory.Exists(_workspace.Beside("nowhere")));
    }

    [Fact]
    public void List_prints_state_size_and_root()
    {
        var live = Index(_workspace.Root);
        var orphan = Orphan("gone");
        var unrecorded = Unrecorded();

        var lines = _workspace.Run("cache", "list").Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var expected = InListOrder((live, "live"), (orphan, "orphaned"), (null, "unknown"));
        Assert.Equal(expected.Count, lines.Length);
        foreach (var ((root, state), line) in expected.Zip(lines))
        {
            Assert.Matches($@"^{state} +\d+  {Regex.Escape(root ?? unrecorded)}$", line);
        }
    }

    [Fact]
    public void Prune_removes_only_orphaned_indexes()
    {
        var live = Index(_workspace.Root);
        var orphan = Orphan("gone");
        var unreachable = Unreachable();
        Unrecorded();
        var orphanDatabase = Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(orphan), CacheLocation.DatabaseName);
        Assert.True(File.Exists(orphanDatabase));

        var removed = Json(_workspace.Run("cache", "prune", "--json"));

        Assert.Equal([(orphan, "orphaned")], Entries(removed));
        Assert.False(Directory.Exists(Path.GetDirectoryName(orphanDatabase)));
        Assert.Equal(InListOrder((live, "live"), (unreachable, "unreachable"), (null, "unknown")), List());
        Assert.DoesNotContain(Directory.EnumerateDirectories(_workspace.CacheDir), folder => folder.EndsWith(".removing", StringComparison.Ordinal));
    }

    [Fact]
    public void Prune_dry_run_removes_nothing()
    {
        var orphan = Orphan("gone");

        var removed = Json(_workspace.Run("cache", "prune", "--dry-run", "--json"));
        var text = _workspace.Run("cache", "prune", "--dry-run");

        Assert.Equal([(orphan, "orphaned")], Entries(removed));
        Assert.Contains($"Would remove {orphan} (", text.Stdout);
        Assert.Contains("Would remove 1 index, ", text.Stdout);
        Assert.Equal([(orphan, "orphaned")], List());
    }

    [Fact]
    public void Prune_include_unreachable_removes_unreachable_indexes_too()
    {
        var live = Index(_workspace.Root);
        var orphan = Orphan("gone");
        var unreachable = Unreachable();
        Unrecorded();

        var removed = Json(_workspace.Run("cache", "prune", "--include-unreachable", "--json"));

        Assert.Equal(InListOrder((orphan, "orphaned"), (unreachable, "unreachable")), Entries(removed));
        Assert.Equal(InListOrder((live, "live"), (null, "unknown")), List());
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public void An_index_whose_root_cannot_be_checked_is_unreachable_and_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var root = Index(_workspace.AddWorkspace(Path.Combine("locked", "notes")));
        var locked = _workspace.Beside("locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            try
            {
                _ = Directory.EnumerateFileSystemEntries(locked).ToList();
                Assert.Skip("this user can read a folder it has no permissions on");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Equal([(root, "unreachable")], List());
            Assert.Empty(Json(_workspace.Run("cache", "prune", "--json")).EnumerateArray());
            Assert.Equal([(root, "unreachable")], List());
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void List_opens_indexes_read_only_and_never_migrates_them()
    {
        var live = Index(_workspace.Root);
        var database = Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(live), CacheLocation.DatabaseName);
        // Marks the index as older than every migration, so migrating it would change it or fail.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database);

        Assert.Equal([(live, "live")], List());
        Assert.Equal(before, File.ReadAllBytes(database));
    }

    [Fact]
    public void Prune_prints_what_it_removed()
    {
        var orphan = Orphan("gone");

        var first = _workspace.Run("cache", "prune");
        var second = _workspace.Run("cache", "prune");

        Assert.Equal(ExitCode.Clean, first.ExitCode);
        Assert.Matches($@"^Removed {Regex.Escape(orphan)} \(\d+ bytes\)\r?\nRemoved 1 index, \d+ bytes\.\r?\n$", first.Stdout);
        Assert.Equal("Nothing to remove.", second.Stdout.TrimEnd());
    }

    [Fact]
    public void Prune_deletes_what_an_earlier_prune_left_half_removed()
    {
        var leftover = Path.Combine(_workspace.CacheDir, $"{new string('a', 64)}.{Guid.NewGuid():n}.removing");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, CacheLocation.DatabaseName), "");

        // Folders that Remove would not have named so are not hippo's to delete.
        var foreign = Path.Combine(_workspace.CacheDir, "notes.removing");
        var unguided = Path.Combine(_workspace.CacheDir, $"{new string('a', 64)}.removing");
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(unguided);

        var preview = _workspace.Run("cache", "prune", "--dry-run");
        Assert.Equal(ExitCode.Clean, preview.ExitCode);
        Assert.True(Directory.Exists(leftover));
        var result = _workspace.Run("cache", "prune");

        Assert.Equal(ExitCode.Clean, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.False(Directory.Exists(leftover));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(unguided));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Skipped there.
    public void Prune_reports_an_index_it_renamed_but_could_not_delete_and_goes_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");
        var stuck = Orphan("gone-a");
        var other = Orphan("gone-b");
        // A read-only folder in the index lets prune rename the index folder but not delete it.
        var readOnly = Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(stuck), "read-only");
        Directory.CreateDirectory(readOnly);
        File.WriteAllText(Path.Combine(readOnly, "file"), "");
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                File.Delete(Path.Combine(readOnly, "file"));
                Assert.Skip("this user can delete from a read-only folder");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var first = _workspace.Run("cache", "prune", "--json");
            var second = _workspace.Run("cache", "prune");

            Assert.Equal(InListOrder((stuck, "orphaned"), (other, "orphaned")), Entries(Json(first)));
            Assert.Contains("hippo: warning: cannot delete ", first.Stderr);
            Assert.Empty(List());
            Assert.Equal(ExitCode.Clean, second.ExitCode);
            Assert.Contains("hippo: warning: cannot remove ", second.Stderr);
        }
        finally
        {
            foreach (var folder in Directory.EnumerateDirectories(_workspace.CacheDir, "read-only", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var third = _workspace.Run("cache", "prune");

        Assert.Equal(ExitCode.Clean, third.ExitCode);
        Assert.Equal("", third.Stderr);
        Assert.Empty(Directory.EnumerateDirectories(_workspace.CacheDir));
    }

    [Fact]
    public void Prune_keeps_an_index_that_is_open_and_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to rename a folder while a file in it is open");
        var orphan = Orphan("gone");
        var database = Path.Combine(_workspace.CacheDir, CacheLocation.FolderName(orphan), CacheLocation.DatabaseName);

        TestWorkspace.Result result;
        using (IndexDatabase.Open(database))
        {
            result = _workspace.Run("cache", "prune");
        }

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Contains($"hippo: cannot remove the index of {orphan}: ", result.Stderr);
        Assert.Equal([(orphan, "orphaned")], List());
    }

    [Fact]
    public void A_relative_hippo_cache_dir_is_an_error()
    {
        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = "cache" }, "cache", "list");

        Assert.Equal(ExitCode.Error, result.ExitCode);
        Assert.Contains("HIPPO_CACHE_DIR", result.Stderr);
    }

    private static void CopyFolder(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
