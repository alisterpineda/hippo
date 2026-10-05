using System.Text.Json;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Integration;

public sealed class CommandTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(TestWorkspace.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void Index_prints_a_summary()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.txt", "b");

        var result = _workspace.Run("index");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^Indexed 2 files in \d+ ms: 2 added, 0 updated, 0 removed\.$", result.Stdout.TrimEnd());
    }

    [Fact]
    public void Index_json_reports_the_sweep()
    {
        _workspace.Write("a.md", "# A\n");

        var json = Json(_workspace.Run("index", "--json"));

        Assert.Equal(1, json.GetProperty("files").GetInt32());
        Assert.Equal(1, json.GetProperty("added").GetInt32());
    }

    [Fact]
    public void A_new_database_gets_a_full_reindex_and_later_sweeps_do_not()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Settle();

        var first = Json(_workspace.Run("index", "--json"));
        var second = Json(_workspace.Run("index", "--json"));

        Assert.True(first.GetProperty("rebuilt").GetBoolean());
        Assert.False(second.GetProperty("rebuilt").GetBoolean());
        Assert.Equal(0, second.GetProperty("hashed").GetInt32());
    }

    [Fact]
    public void Index_rebuild_rereads_every_file()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Run("index");

        var json = Json(_workspace.Run("index", "--rebuild", "--json"));

        Assert.True(json.GetProperty("rebuilt").GetBoolean());
        Assert.Equal(1, json.GetProperty("hashed").GetInt32());
        Assert.Equal(0, json.GetProperty("added").GetInt32());
    }

    [Fact]
    public void Index_rebuild_says_it_rebuilt_the_index_rather_than_counting_changes()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("b.txt", "b");
        _workspace.Run("index");

        var result = _workspace.Run("index", "--rebuild");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^Rebuilt the index from 2 files in \d+ ms\.$", result.Stdout.TrimEnd());
    }

    [Fact]
    public void Index_says_it_rebuilt_the_index_when_a_migration_rebuilt_it()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Settle();
        _workspace.Run("index");
        _workspace.RollBackIndexTo(5);

        var result = _workspace.Run("index");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^Rebuilt the index from 1 files in \d+ ms\.$", result.Stdout.TrimEnd());
    }

    /// <summary>A migration whose first sweep was interrupted leaves its marker behind, and the next run rebuilds as the
    /// interrupted one would have, then clears it.</summary>
    [Fact]
    public void Index_rebuilds_an_index_left_marked_for_a_rebuild_and_then_clears_the_marker()
    {
        _workspace.Write("a.md", "# A\n");
        _workspace.Settle();
        _workspace.Run("index");
        var database = Json(_workspace.Run("status", "--json")).GetProperty("database").GetString()!;
        using (var connection = Connect(database))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@key, '1')";
            insert.Parameters.AddWithValue("@key", IndexMeta.RebuildPending);
            insert.ExecuteNonQuery();
        }

        var rebuilt = _workspace.Run("index");
        var again = _workspace.Run("index");

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Matches(@"^Rebuilt the index from 1 files in \d+ ms\.$", rebuilt.Stdout.TrimEnd());
        Assert.Matches("^Indexed ", again.Stdout);
        using (var connection = Connect(database))
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM meta WHERE key = @key";
            count.Parameters.AddWithValue("@key", IndexMeta.RebuildPending);
            Assert.Equal(0L, count.ExecuteScalar());
        }
    }

    private static SqliteConnection Connect(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public void The_first_command_builds_the_index_in_the_cache_dir()
    {
        var json = Json(_workspace.Run("status", "--json"));

        var database = json.GetProperty("database").GetString()!;
        Assert.Equal(_workspace.CacheDir, Path.GetDirectoryName(Path.GetDirectoryName(database)));
        Assert.Equal("index.db", Path.GetFileName(database));
        Assert.True(File.Exists(database));
    }

    [Fact]
    public void A_relative_hippo_cache_dir_is_an_error()
    {
        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = "cache" }, "status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("HIPPO_CACHE_DIR", result.Stderr);
        Assert.False(Directory.Exists(_workspace.Combine("cache")));
    }

    [Fact]
    public void Without_hippo_cache_dir_the_index_is_in_the_user_cache_folder()
    {
        // The cache folder stands in for the user's home, so nothing lands in the real one.
        var home = _workspace.CacheDir;
        var json = Json(_workspace.RunWith(new Dictionary<string, string> { ["HOME"] = home, ["LOCALAPPDATA"] = home }, "status", "--json"));

        var expected = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "hippo")
            : OperatingSystem.IsWindows() ? Path.Combine(home, "hippo")
            : Path.Combine(home, ".cache", "hippo");
        var database = json.GetProperty("database").GetString()!;
        Assert.Equal(expected, Path.GetDirectoryName(Path.GetDirectoryName(database)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(database)!));
        }
    }

    [Fact]
    public void Updated_and_removed_files_are_counted_apart()
    {
        var edited = _workspace.Write("a.md", "# A\n");
        foreach (var name in new[] { "b", "c", "d", "e", "f", "g" })
        {
            _workspace.Write($"{name}.md", $"# {name}\n");
        }
        _workspace.Run("index");

        // Each round edits one file and deletes two, so a swapped count shows.
        TestWorkspace.Result Round(string content, string gone1, string gone2, params string[] args)
        {
            File.WriteAllText(edited, content);
            File.Delete(_workspace.Combine(gone1));
            File.Delete(_workspace.Combine(gone2));
            return _workspace.Run(args);
        }
        var text = Round("# A, edited\n", "b.md", "c.md", "index");
        var index = Json(Round("# A, edited twice\n", "d.md", "e.md", "index", "--json"));
        var status = Json(Round("# A, edited three times\n", "f.md", "g.md", "status", "--json")).GetProperty("lastSweep");

        Assert.Contains("0 added, 1 updated, 2 removed.", text.Stdout);
        Assert.Equal((0, 1, 2), (index.GetProperty("added").GetInt32(), index.GetProperty("updated").GetInt32(), index.GetProperty("removed").GetInt32()));
        Assert.Equal((0, 1, 2), (status.GetProperty("added").GetInt32(), status.GetProperty("updated").GetInt32(), status.GetProperty("removed").GetInt32()));
    }

    [Fact]
    public void Status_reports_root_database_counts_and_last_sweep()
    {
        _workspace.Write("a.md", "---\ntitle: A\n---\n");
        _workspace.Write("bad.md", "---\ntitle: [\n---\n");
        _workspace.Write("c.png", "png");

        var result = _workspace.Run("status");

        Assert.Equal(0, result.ExitCode);
        var lines = Lines(result.Stdout);
        Assert.Contains($"Workspace:   {_workspace.Root}", lines);
        Assert.Contains(lines, l => l.StartsWith($"Database:    {_workspace.CacheDir}", StringComparison.Ordinal));
        Assert.Contains("Files:       3 (2 markdown, 1 other)", lines);
        Assert.Contains("Frontmatter: 1 with errors (hippo find --errors)", lines);
        Assert.Contains(lines, l => l.StartsWith("Last sweep:  ", StringComparison.Ordinal) && l.Contains("3 added"));
    }

    [Fact]
    public void Status_points_to_nothing_when_no_frontmatter_has_errors()
    {
        _workspace.Write("a.md", "---\ntitle: A\n---\n");

        var result = _workspace.Run("status");

        Assert.Contains("Frontmatter: 0 with errors", Lines(result.Stdout));
    }

    [Fact]
    public void Status_json_has_the_same_facts()
    {
        _workspace.Write("a.md", "# A\n");

        var json = Json(_workspace.Run("status", "--json"));

        Assert.Equal(_workspace.Root, json.GetProperty("root").GetString());
        var files = json.GetProperty("files");
        Assert.Equal((1, 1, 0, 0), (files.GetProperty("total").GetInt32(), files.GetProperty("markdown").GetInt32(),
            files.GetProperty("other").GetInt32(), files.GetProperty("parseErrors").GetInt32()));
        var sweep = json.GetProperty("lastSweep");
        Assert.Equal(1, sweep.GetProperty("added").GetInt32());
        Assert.True(sweep.GetProperty("finishedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void Control_characters_in_the_status_paths_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        var cache = Path.Combine(_workspace.CacheDir, "c\u001b]0;T\u0007");

        var result = _workspace.RunWith(new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = cache }, "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout),
            l => l.StartsWith($"Database:    {_workspace.CacheDir}{Path.DirectorySeparatorChar}c\\x1b]0;T\\x07", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public void Control_characters_in_the_status_workspace_root_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        _workspace.Write("w\u001b]0;T\u0007/.hippo/config.json", "");

        var result = _workspace.RunIn(_workspace.Combine("w\u001b]0;T\u0007"), "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout),
            l => l == $"Workspace:   {_workspace.Root}{Path.DirectorySeparatorChar}w\\x1b]0;T\\x07");
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public void Control_characters_in_an_error_are_escaped()
    {
        var result = _workspace.Run("show", "n\u001b]0;T\u0007.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("hippo: n\\x1b]0;T\\x07.md is not in the index", result.Stderr);
        Assert.DoesNotContain('\u001b', result.Stderr);
    }

    [Fact]
    public void Show_prints_a_files_row()
    {
        _workspace.Write("a.md", "---\ntitle: A\n---\n# A\n");

        var result = _workspace.Run("show", "a.md");

        Assert.Equal(0, result.ExitCode);
        var lines = Lines(result.Stdout);
        Assert.Contains("Path:        a.md", lines);
        Assert.Contains("Kind:        markdown", lines);
        Assert.Contains("Size:        21 bytes", lines);
        Assert.Contains(lines, l => l.StartsWith("Modified:    ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Hash:        ", StringComparison.Ordinal));
        Assert.Contains("\"title\": \"A\"", lines);
    }

    [Fact]
    public void Show_prints_frontmatter_text_as_written_but_escapes_control_characters()
    {
        _workspace.Write("a.md", "---\ntitle: The human's café 🏠\nnote: \"\\e]0;T\\a\"\n---\n");

        var lines = Lines(_workspace.Run("show", "a.md").Stdout);

        Assert.Contains("\"title\": \"The human's café 🏠\",", lines);
        Assert.Contains("\"note\": \"\\u001B]0;T\\u0007\"", lines);
    }

    [Fact]
    public void Show_json_includes_frontmatter_as_an_object()
    {
        _workspace.Write("a.md", "---\ntitle: A\ntags: [x]\n---\n");

        var json = Json(_workspace.Run("show", "a.md", "--json"));

        Assert.Equal("a.md", json.GetProperty("path").GetString());
        Assert.Equal("A", json.GetProperty("frontmatter").GetProperty("title").GetString());
        Assert.Equal("x", json.GetProperty("frontmatter").GetProperty("tags")[0].GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("parseError").ValueKind);
    }

    [Fact]
    public void A_same_size_edit_within_the_mtime_tick_of_the_last_sweep_is_seen()
    {
        // A future mtime is always inside the racy margin, so the test needs no timing luck.
        var mtime = DateTime.UtcNow.AddMinutes(1);
        var path = _workspace.Write("a.md", "---\ntitle: A\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);
        _workspace.Run("index");
        File.WriteAllText(path, "---\ntitle: B\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);

        var json = Json(_workspace.Run("show", "a.md", "--json"));

        Assert.Equal("B", json.GetProperty("frontmatter").GetProperty("title").GetString());
    }

    [Fact]
    public void Show_reports_malformed_frontmatter_without_failing()
    {
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var index = _workspace.Run("index");
        var json = Json(_workspace.Run("show", "bad.md", "--json"));

        Assert.Equal(0, index.ExitCode);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("frontmatter").ValueKind);
        Assert.StartsWith("line ", json.GetProperty("parseError").GetString());
    }

    [Fact]
    public void Malformed_frontmatter_warns_when_the_page_is_read_and_not_again_while_unchanged()
    {
        _workspace.Write("good.md", "---\ntitle: A\n---\n");
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var first = _workspace.Run("find");
        var second = _workspace.Run("find");

        Assert.Equal(0, first.ExitCode);
        var warning = Assert.Single(Lines(first.Stderr));
        Assert.StartsWith("hippo: warning: cannot read the frontmatter in bad.md: line ", warning);
        Assert.Equal("", second.Stderr);
    }

    [Fact]
    public void Show_emits_frontmatter_nested_as_deeply_as_the_index_keeps()
    {
        _workspace.Write("deep.md", $"---\na: {new string('[', 60)}{new string(']', 60)}\n---\n");

        var result = _workspace.Run("show", "deep.md", "--json");
        var json = Json(result);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("parseError").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("frontmatter").GetProperty("a").ValueKind);
    }

    [Fact]
    public void Show_prints_a_parse_error_as_text()
    {
        _workspace.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var result = _workspace.Run("show", "bad.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout), l => l.StartsWith("Parse error: line ", StringComparison.Ordinal));
    }

    [Fact]
    public void Show_says_when_a_markdown_file_has_no_frontmatter()
    {
        _workspace.Write("plain.md", "# Just a heading\n");

        var result = _workspace.Run("show", "plain.md");

        Assert.Contains("Frontmatter: none", Lines(result.Stdout));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..")]
    [InlineData(".")]
    public void Show_of_a_path_outside_the_workspace_is_an_error(string path)
    {
        var result = _workspace.Run("show", path);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"is not a file inside the workspace at {_workspace.Root}", result.Stderr);
    }

    [Fact]
    public void Show_of_a_path_not_in_the_index_is_an_error()
    {
        var result = _workspace.Run("show", "missing.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing.md", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void A_subfolder_uses_the_workspace_root_and_resolves_paths_from_itself()
    {
        _workspace.Write("wiki/a.md", "# A\n");

        var status = Json(_workspace.RunIn(_workspace.Combine("wiki"), "status", "--json"));
        var show = Json(_workspace.RunIn(_workspace.Combine("wiki"), "show", "a.md", "--json"));

        Assert.Equal(_workspace.Root, status.GetProperty("root").GetString());
        Assert.Equal("wiki/a.md", show.GetProperty("path").GetString());
    }

    [Fact]
    public void An_edit_shows_up_on_the_next_command()
    {
        var path = _workspace.Write("a.md", "---\ntitle: Before\n---\n");
        _workspace.Run("index");

        File.WriteAllText(path, "---\ntitle: After the edit\n---\n");
        _workspace.Write("new.md", "# New\n");
        var show = Json(_workspace.Run("show", "a.md", "--json"));
        var files = _workspace.Run("find");

        Assert.Equal("After the edit", show.GetProperty("frontmatter").GetProperty("title").GetString());
        Assert.Contains("new.md", Lines(files.Stdout));
    }

    [Fact]
    public void A_deleted_file_is_gone_on_the_next_command()
    {
        var path = _workspace.Write("a.md", "# A\n");
        _workspace.Run("index");

        File.Delete(path);
        var files = _workspace.Run("find");

        Assert.DoesNotContain("a.md", Lines(files.Stdout));
    }

    [Fact]
    public void Excluded_files_are_not_listed()
    {
        _workspace.Write(".hippo/config.json", """{ "files": { "include": ["**/*"], "exclude": ["inbox/**"] } }""");
        _workspace.Write("a.md", "# A\n");
        _workspace.Write("inbox/b.md", "# B\n");

        var files = _workspace.Run("find");

        Assert.Equal(["a.md"], Lines(files.Stdout));
    }

    [Fact]
    public void A_misspelt_key_is_an_error_that_names_it()
    {
        _workspace.Write(".hippo/config.json", """{ "files": { "exlcude": ["inbox/**"] } }""");

        var result = _workspace.Run("status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(".hippo/config.json: unknown key files.exlcude; expected include or exclude", result.Stderr);
    }

    [Fact]
    public void A_malformed_config_is_an_error()
    {
        _workspace.Write(".hippo/config.json", """{ "files": ["**/*"] }""");

        var result = _workspace.Run("status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(".hippo/config.json", result.Stderr);
    }

    [Fact]
    public void Without_a_workspace_every_command_is_an_error()
    {
        File.Delete(_workspace.Combine(".hippo/config.json"));

        foreach (var command in new[] { "index", "status", "find" })
        {
            var result = _workspace.Run(command);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains(".hippo/config.json", result.Stderr);
        }
    }
}
