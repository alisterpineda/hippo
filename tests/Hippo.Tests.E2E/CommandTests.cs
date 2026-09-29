using System.Text.Json;

namespace Hippo.Tests.E2E;

public sealed class CommandTests : IDisposable
{
    private readonly TempNotebook _notebook = new();

    public void Dispose() => _notebook.Dispose();

    private static JsonElement Json(HippoProcess.Result result)
    {
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout).RootElement;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task Index_prints_a_summary()
    {
        _notebook.Write("a.md", "# A\n");
        _notebook.Write("b.txt", "b");

        var result = await _notebook.RunAsync("index");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^Indexed 3 files in \d+ ms: 3 added, 0 updated, 0 removed\.$", result.Stdout.TrimEnd());
    }

    [Fact]
    public async Task Index_json_reports_the_sweep()
    {
        _notebook.Write("a.md", "# A\n");

        var json = Json(await _notebook.RunAsync("index", "--json"));

        Assert.Equal(2, json.GetProperty("files").GetInt32());
        Assert.Equal(2, json.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task A_new_database_gets_a_full_reindex_and_later_sweeps_do_not()
    {
        _notebook.Write("a.md", "# A\n");
        _notebook.Settle();

        var first = Json(await _notebook.RunAsync("index", "--json"));
        var second = Json(await _notebook.RunAsync("index", "--json"));

        Assert.True(first.GetProperty("rebuilt").GetBoolean());
        Assert.False(second.GetProperty("rebuilt").GetBoolean());
        Assert.Equal(0, second.GetProperty("hashed").GetInt32());
    }

    [Fact]
    public async Task Index_rebuild_rereads_every_file()
    {
        _notebook.Write("a.md", "# A\n");
        await _notebook.RunAsync("index");

        var json = Json(await _notebook.RunAsync("index", "--rebuild", "--json"));

        Assert.True(json.GetProperty("rebuilt").GetBoolean());
        Assert.Equal(2, json.GetProperty("hashed").GetInt32());
        Assert.Equal(0, json.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task The_first_command_builds_the_index_in_the_cache_dir()
    {
        var json = Json(await _notebook.RunAsync("status", "--json"));

        var database = json.GetProperty("database").GetString()!;
        Assert.Equal(_notebook.CacheDir, Path.GetDirectoryName(Path.GetDirectoryName(database)));
        Assert.Equal("index.db", Path.GetFileName(database));
        Assert.True(File.Exists(database));
    }

    [Fact]
    public async Task Without_hippo_cache_dir_the_index_is_in_the_user_cache_folder()
    {
        // The cache folder stands in for the user's home, so nothing lands in the real one.
        var home = _notebook.CacheDir;
        var json = Json(await _notebook.RunWithAsync(
            new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = "", ["XDG_CACHE_HOME"] = "", ["HOME"] = home, ["LOCALAPPDATA"] = home },
            "status", "--json"));

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
    public async Task A_relative_hippo_cache_dir_is_an_error()
    {
        var result = await _notebook.RunWithAsync(new Dictionary<string, string> { ["HIPPO_CACHE_DIR"] = "cache" }, "status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("HIPPO_CACHE_DIR", result.Stderr);
        Assert.False(Directory.Exists(_notebook.Combine("cache")));
    }

    [Fact]
    public async Task Updated_and_removed_files_are_counted_apart()
    {
        var edited = _notebook.Write("a.md", "# A\n");
        foreach (var name in new[] { "b", "c", "d", "e", "f", "g" })
        {
            _notebook.Write($"{name}.md", $"# {name}\n");
        }
        await _notebook.RunAsync("index");

        // Each round edits one file and deletes two, so a swapped count shows.
        async Task<HippoProcess.Result> Round(string content, string gone1, string gone2, params string[] args)
        {
            File.WriteAllText(edited, content);
            File.Delete(_notebook.Combine(gone1));
            File.Delete(_notebook.Combine(gone2));
            return await _notebook.RunAsync(args);
        }
        var text = await Round("# A, edited\n", "b.md", "c.md", "index");
        var index = Json(await Round("# A, edited twice\n", "d.md", "e.md", "index", "--json"));
        var status = Json(await Round("# A, edited three times\n", "f.md", "g.md", "status", "--json")).GetProperty("lastSweep");

        Assert.Contains("0 added, 1 updated, 2 removed.", text.Stdout);
        Assert.Equal((0, 1, 2), (index.GetProperty("added").GetInt32(), index.GetProperty("updated").GetInt32(), index.GetProperty("removed").GetInt32()));
        Assert.Equal((0, 1, 2), (status.GetProperty("added").GetInt32(), status.GetProperty("updated").GetInt32(), status.GetProperty("removed").GetInt32()));
    }

    [Fact]
    public async Task Status_reports_root_database_counts_and_last_sweep()
    {
        _notebook.Write("a.md", "---\ntitle: A\n---\n");
        _notebook.Write("bad.md", "---\ntitle: [\n---\n");
        _notebook.Write("c.png", "png");

        var result = await _notebook.RunAsync("status");

        Assert.Equal(0, result.ExitCode);
        var lines = Lines(result.Stdout);
        Assert.Contains($"Notebook:    {_notebook.Root}", lines);
        Assert.Contains(lines, l => l.StartsWith($"Database:    {_notebook.CacheDir}", StringComparison.Ordinal));
        Assert.Contains("Files:       4 (2 markdown, 2 plain)", lines);
        Assert.Contains("Frontmatter: 1 with errors", lines);
        Assert.Contains(lines, l => l.StartsWith("Last sweep:  ", StringComparison.Ordinal) && l.Contains("4 added"));
    }

    [Fact]
    public async Task Status_json_has_the_same_facts()
    {
        _notebook.Write("a.md", "# A\n");

        var json = Json(await _notebook.RunAsync("status", "--json"));

        Assert.Equal(_notebook.Root, json.GetProperty("root").GetString());
        var files = json.GetProperty("files");
        Assert.Equal((2, 1, 1, 0), (files.GetProperty("total").GetInt32(), files.GetProperty("markdown").GetInt32(),
            files.GetProperty("plain").GetInt32(), files.GetProperty("parseErrors").GetInt32()));
        var sweep = json.GetProperty("lastSweep");
        Assert.Equal(2, sweep.GetProperty("added").GetInt32());
        Assert.True(sweep.GetProperty("finishedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Files_lists_every_indexed_path()
    {
        _notebook.Write("wiki/a.md", "# A\n");
        _notebook.Write("raw/b.txt", "b");

        var result = await _notebook.RunAsync("files");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([".hippo.yaml", "raw/b.txt", "wiki/a.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Files_glob_filters_by_notebook_relative_pattern()
    {
        _notebook.Write("wiki/a.md", "# A\n");
        _notebook.Write("wiki/deep/b.md", "# B\n");
        _notebook.Write("raw/c.md", "# C\n");

        var result = await _notebook.RunAsync("files", "--glob", "wiki/**/*.md");

        Assert.Equal(["wiki/a.md", "wiki/deep/b.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Files_where_filters_by_frontmatter_value()
    {
        _notebook.Write("wiki/topic.md", "---\ntype: Topic\n---\n");
        _notebook.Write("wiki/person.md", "---\ntype: Person\n---\n");

        var result = await _notebook.RunAsync("files", "--where", "type=Topic");

        Assert.Equal(["wiki/topic.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Files_glob_and_where_combine()
    {
        _notebook.Write("wiki/topic.md", "---\ntype: Topic\n---\n");
        _notebook.Write("drafts/topic.md", "---\ntype: Topic\n---\n");
        _notebook.Write("wiki/person.md", "---\ntype: Person\n---\n");

        var result = await _notebook.RunAsync("files", "--glob", "wiki/**", "--where", "type=Topic");

        Assert.Equal(["wiki/topic.md"], Lines(result.Stdout));
    }

    [Fact]
    public async Task Files_json_lists_path_kind_size_and_modified()
    {
        _notebook.Write("a.md", "# A\n");

        var json = Json(await _notebook.RunAsync("files", "--glob", "*.md", "--json"));

        var file = Assert.Single(json.EnumerateArray());
        Assert.Equal("a.md", file.GetProperty("path").GetString());
        Assert.Equal("markdown", file.GetProperty("kind").GetString());
        Assert.Equal(4, file.GetProperty("size").GetInt64());
        Assert.Equal(File.GetLastWriteTimeUtc(_notebook.Combine("a.md")), file.GetProperty("modified").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public async Task Files_with_a_malformed_where_is_an_error()
    {
        var result = await _notebook.RunAsync("files", "--where", "type");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--where", result.Stderr);
    }

    [Fact]
    public async Task Show_prints_a_files_row()
    {
        _notebook.Write("a.md", "---\ntitle: A\n---\n# A\n");

        var result = await _notebook.RunAsync("show", "a.md");

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
    public async Task Show_json_includes_frontmatter_as_an_object()
    {
        _notebook.Write("a.md", "---\ntitle: A\ntags: [x]\n---\n");

        var json = Json(await _notebook.RunAsync("show", "a.md", "--json"));

        Assert.Equal("a.md", json.GetProperty("path").GetString());
        Assert.Equal("A", json.GetProperty("frontmatter").GetProperty("title").GetString());
        Assert.Equal("x", json.GetProperty("frontmatter").GetProperty("tags")[0].GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("parseError").ValueKind);
    }

    [Fact]
    public async Task A_same_size_edit_within_the_mtime_tick_of_the_last_sweep_is_seen()
    {
        // A future mtime is always inside the racy margin, so the test needs no timing luck.
        var mtime = DateTime.UtcNow.AddMinutes(1);
        var path = _notebook.Write("a.md", "---\ntitle: A\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);
        await _notebook.RunAsync("index");
        File.WriteAllText(path, "---\ntitle: B\n---\n");
        File.SetLastWriteTimeUtc(path, mtime);

        var json = Json(await _notebook.RunAsync("show", "a.md", "--json"));

        Assert.Equal("B", json.GetProperty("frontmatter").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Show_reports_malformed_frontmatter_without_failing()
    {
        _notebook.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var index = await _notebook.RunAsync("index");
        var json = Json(await _notebook.RunAsync("show", "bad.md", "--json"));

        Assert.Equal(0, index.ExitCode);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("frontmatter").ValueKind);
        Assert.StartsWith("line ", json.GetProperty("parseError").GetString());
    }

    [Fact]
    public async Task Show_prints_a_parse_error_as_text()
    {
        _notebook.Write("bad.md", "---\ntitle: [unclosed\n---\n");

        var result = await _notebook.RunAsync("show", "bad.md");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Lines(result.Stdout), l => l.StartsWith("Parse error: line ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Show_says_when_a_markdown_file_has_no_frontmatter()
    {
        _notebook.Write("plain.md", "# Just a heading\n");

        var result = await _notebook.RunAsync("show", "plain.md");

        Assert.Contains("Frontmatter: none", Lines(result.Stdout));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task Show_of_a_path_outside_the_notebook_is_an_error(string path)
    {
        var result = await _notebook.RunAsync("show", path);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"is not a file inside the notebook at {_notebook.Root}", result.Stderr);
    }

    [Fact]
    public async Task Control_characters_in_file_names_are_escaped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold control characters");
        _notebook.Write("n\u001b]0;T\u0007.md", "# N\n");

        var result = await _notebook.RunAsync("files");

        Assert.Contains("n\\x1b]0;T\\x07.md", Lines(result.Stdout));
        Assert.DoesNotContain('\u001b', result.Stdout);
    }

    [Fact]
    public async Task Show_of_a_path_not_in_the_index_is_an_error()
    {
        var result = await _notebook.RunAsync("show", "missing.md");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing.md", result.Stderr);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task A_subfolder_uses_the_notebook_root_and_resolves_paths_from_itself()
    {
        _notebook.Write("wiki/a.md", "# A\n");

        var status = Json(await _notebook.RunInAsync(_notebook.Combine("wiki"), "status", "--json"));
        var show = Json(await _notebook.RunInAsync(_notebook.Combine("wiki"), "show", "a.md", "--json"));

        Assert.Equal(_notebook.Root, status.GetProperty("root").GetString());
        Assert.Equal("wiki/a.md", show.GetProperty("path").GetString());
    }

    [Fact]
    public async Task An_edit_shows_up_on_the_next_command()
    {
        var path = _notebook.Write("a.md", "---\ntitle: Before\n---\n");
        await _notebook.RunAsync("index");

        File.WriteAllText(path, "---\ntitle: After the edit\n---\n");
        _notebook.Write("new.md", "# New\n");
        var show = Json(await _notebook.RunAsync("show", "a.md", "--json"));
        var files = await _notebook.RunAsync("files");

        Assert.Equal("After the edit", show.GetProperty("frontmatter").GetProperty("title").GetString());
        Assert.Contains("new.md", Lines(files.Stdout));
    }

    [Fact]
    public async Task A_deleted_file_is_gone_on_the_next_command()
    {
        var path = _notebook.Write("a.md", "# A\n");
        await _notebook.RunAsync("index");

        File.Delete(path);
        var files = await _notebook.RunAsync("files");

        Assert.DoesNotContain("a.md", Lines(files.Stdout));
    }

    [Fact]
    public async Task Excluded_files_are_not_listed()
    {
        _notebook.Write(".hippo.yaml", "version: 1\nfiles:\n  include: [\"**/*\"]\n  exclude: [\".hippo.yaml\", \"inbox/**\"]\n");
        _notebook.Write("a.md", "# A\n");
        _notebook.Write("inbox/b.md", "# B\n");

        var files = await _notebook.RunAsync("files");

        Assert.Equal(["a.md"], Lines(files.Stdout));
    }

    [Fact]
    public async Task Keys_from_later_phases_warn_but_the_command_succeeds()
    {
        _notebook.Write(".hippo.yaml", "version: 1\nids:\n  field: sources[].id\n");

        var result = await _notebook.RunAsync("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("warning", result.Stderr);
        Assert.Contains("'ids'", result.Stderr);
    }

    [Fact]
    public async Task A_malformed_config_is_an_error()
    {
        _notebook.Write(".hippo.yaml", "version: 7\n");

        var result = await _notebook.RunAsync("status");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(".hippo.yaml", result.Stderr);
    }

    [Fact]
    public async Task Without_a_notebook_every_command_is_an_error()
    {
        File.Delete(_notebook.Combine(".hippo.yaml"));

        foreach (var command in new[] { "index", "status", "files" })
        {
            var result = await _notebook.RunAsync(command);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains(".hippo.yaml", result.Stderr);
        }
    }

    [Fact]
    public async Task A_usage_error_exits_2()
    {
        var result = await _notebook.RunAsync("files", "--bogus");

        Assert.Equal(2, result.ExitCode);
    }
}
