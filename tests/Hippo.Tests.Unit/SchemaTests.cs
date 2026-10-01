using System.Text.RegularExpressions;
using Dapper;
using Hippo.Indexing;
using Hippo.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hippo.Tests.Unit;

/// <summary>Guards the embedded SQL scripts: their numbering, their pairing with the EF migrations they were exported
/// from, the schema they build and the constraints they declare.</summary>
public class SchemaTests
{
    private static HippoDbContext CreateContext() => new HippoDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Scripts_are_numbered_from_1_without_gaps()
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.Scripts.Count), MigrationRunner.Scripts.Select(s => s.Version));
    }

    [Fact]
    public void Every_migration_has_a_script_and_every_script_a_migration()
    {
        using var context = CreateContext();
        Assert.Equal(context.Database.GetMigrations(), MigrationRunner.Scripts.Select(s => s.Name));
    }

    /// <summary>
    /// The scripts and the EF model must describe one schema, or the next exported migration is diffed against a
    /// schema no index has. A new NOT NULL column fails here until its model declares the default EF wrote to fill the
    /// existing rows.
    /// </summary>
    [Fact]
    public void The_scripts_build_the_schema_the_model_describes()
    {
        using var context = CreateContext();
        using var fromScripts = new TestDatabase();
        using var fromModel = new TestDatabase();
        using var scripts = fromScripts.Open();
        using var model = fromModel.Connect();
        model.Execute(context.Database.GenerateCreateScript());

        var tables = Tables(model);
        Assert.Equal(tables, Tables(scripts));
        foreach (var table in tables)
        {
            Assert.Equal(Describe(model, table), Describe(scripts, table));
        }
    }

    [Fact]
    public void The_scripts_enforce_the_kind_check()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();

        var ex = Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('a', 0, 0, 'h', 0, 'image')"));

        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Theory]
    [InlineData("other", "path", "a.md")]
    [InlineData("body", "other", "a.md")]
    [InlineData("body", "url", "a.md")]
    public void The_scripts_enforce_the_link_checks(string kind, string type, string target)
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        connection.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('a.md', 0, 0, 'h', 0, 'markdown')");

        var ex = Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO links (source_id, line, kind, type, raw, target) VALUES (1, 1, @kind, @type, 'x', @target)", new { kind, type, target }));

        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public void Deleting_a_file_deletes_its_links()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        connection.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('a.md', 0, 0, 'h', 0, 'markdown')");
        connection.Execute("INSERT INTO links (source_id, line, kind, type, raw, target) VALUES (1, 1, 'body', 'path', 'b.md', 'b.md')");

        connection.Execute("DELETE FROM files");

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM links"));
    }

    [Fact]
    public void Deleting_a_file_deletes_its_findings()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        connection.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('a.md', 0, 0, 'h', 0, 'markdown')");
        connection.Execute("INSERT INTO findings (file_id, rule, line, message, related) VALUES (1, 'okf-type', NULL, 'm', '[]')");

        connection.Execute("DELETE FROM files");

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM findings"));
    }

    [Fact]
    public void Deleting_a_file_deletes_its_index_entries()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        connection.Execute("INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('kb/index.md', 0, 0, 'h', 0, 'markdown')");
        connection.Execute("INSERT INTO index_entries (file_id, line, target, description) VALUES (1, 1, 'kb/a.md', NULL)");

        connection.Execute("DELETE FROM files");

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM index_entries"));
    }

    [Fact]
    public void The_scripts_create_a_search_table_that_matches_title_path_and_body_but_not_rowid()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        connection.Execute("INSERT INTO search (rowid, title, path, body) VALUES (12345, 'heron', 'birds/kestrel.md', 'owl')");

        string[] found = ["heron", "kestrel", "owl"];
        Assert.All(found, word => Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM search WHERE search MATCH @word", new { word })));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM search WHERE search MATCH '12345'"));
    }

    /// <summary>The scripts create the search table and the sweep recreates it when the tokenizer changes, so both must
    /// write it alike, or the first tokenizer change drops what a later migration added.</summary>
    [Fact]
    public void The_scripts_create_the_search_table_the_sweep_recreates()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();

        Assert.Equal(
            SearchIndex.Create(Hippo.Workspaces.SearchTokenizer.Porter),
            connection.ExecuteScalar<string>("SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'search'"));
    }

    /// <summary>The ordinary tables. EF cannot model an FTS5 table, so the search table and the shadow tables FTS5 keeps
    /// for it are left to the scripts alone.</summary>
    private static List<string> Tables(SqliteConnection connection) => connection.Query<string>(
        "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").AsList();

    /// <summary>Columns, indexes, foreign keys and CHECK constraints, as SQLite reports them, in a form that compares
    /// as text. Columns are listed by name: <c>ALTER TABLE ADD COLUMN</c> appends and EF's table rebuilds sort, and
    /// nothing in hippo reads a column by position. No pragma reports CHECK constraints, so they are read from the
    /// table's DDL and listed by name.</summary>
    private static string Describe(SqliteConnection connection, string table)
    {
        var lines = new List<string>
        {
            $"autoincrement={connection.ExecuteScalar<long>("SELECT sql LIKE '%AUTOINCREMENT%' FROM sqlite_schema WHERE type = 'table' AND name = @table", new { table })}",
        };
        using (var reader = connection.ExecuteReader($"SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('{table}') ORDER BY name"))
        {
            while (reader.Read())
            {
                lines.Add($"column {reader[0]} {reader[1]} notnull={reader[2]} default={reader[3]} pk={reader[4]} hidden={reader[5]}");
            }
        }
        using (var reader = connection.ExecuteReader($"""
            SELECT il.name, il."unique", il.partial, group_concat(ii.name, ',')
            FROM pragma_index_list('{table}') il JOIN pragma_index_info(il.name) ii
            WHERE il.origin = 'c' GROUP BY il.name ORDER BY il.name
            """))
        {
            while (reader.Read())
            {
                lines.Add($"index {reader[0]} unique={reader[1]} partial={reader[2]} ({reader[3]})");
            }
        }
        using (var reader = connection.ExecuteReader($"SELECT \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list('{table}') ORDER BY id, seq"))
        {
            while (reader.Read())
            {
                lines.Add($"fk {reader[1]} -> {reader[0]}.{reader[2]} update={reader[3]} delete={reader[4]}");
            }
        }
        var sql = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = @table", new { table })!;
        lines.AddRange(Regex.Matches(sql, @"CONSTRAINT\s+""([^""]+)""\s+CHECK\s+(.+?),?\s*$", RegexOptions.Multiline)
            .Select(m => $"check {m.Groups[1].Value} {m.Groups[2].Value}")
            .Order(StringComparer.Ordinal));
        return string.Join('\n', lines);
    }
}
