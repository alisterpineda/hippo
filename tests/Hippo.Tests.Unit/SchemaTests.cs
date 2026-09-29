using Dapper;
using Hippo.Indexing;
using Microsoft.Data.Sqlite;

namespace Hippo.Tests.Unit;

/// <summary>Guards the embedded SQL scripts: their numbering, the schema they build and the constraints they declare.</summary>
public class SchemaTests
{
    [Fact]
    public void Scripts_are_numbered_from_1_without_gaps()
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.Scripts.Count), MigrationRunner.Scripts.Select(s => s.Version));
    }

    /// <summary>
    /// Pins the schema the scripts build, so a hand-written script that loosens a column, drops an index or leaves a stray
    /// table fails here. A deliberate schema change updates the expected text.
    /// </summary>
    [Fact]
    public void The_scripts_build_the_expected_schema()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();

        var tables = connection.Query<string>(
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Equal(["files", "links", "meta"], tables);
        Assert.Equal(
            """
            column id INTEGER notnull=1 default= pk=1 hidden=0
            column source_id INTEGER notnull=1 default= pk=0 hidden=0
            column line INTEGER notnull=1 default= pk=0 hidden=0
            column kind TEXT notnull=1 default= pk=0 hidden=0
            column type TEXT notnull=1 default= pk=0 hidden=0
            column raw TEXT notnull=1 default= pk=0 hidden=0
            column target TEXT notnull=0 default= pk=0 hidden=0
            index ix_links_source_id unique=0 partial=0 (source_id)
            index ix_links_target unique=0 partial=0 (target)
            fk source_id -> files.id update=NO ACTION delete=CASCADE
            """.ReplaceLineEndings("\n"),
            Describe(connection, "links"));
        Assert.Equal(
            """
            column key TEXT notnull=1 default= pk=1 hidden=0
            column value TEXT notnull=1 default= pk=0 hidden=0
            """.ReplaceLineEndings("\n"),
            Describe(connection, "meta"));
        Assert.Equal(
            """
            column id INTEGER notnull=1 default= pk=1 hidden=0
            column path TEXT notnull=1 default= pk=0 hidden=0
            column mtime INTEGER notnull=1 default= pk=0 hidden=0
            column size INTEGER notnull=1 default= pk=0 hidden=0
            column hash TEXT notnull=1 default= pk=0 hidden=0
            column hashed_at INTEGER notnull=1 default= pk=0 hidden=0
            column kind TEXT notnull=1 default= pk=0 hidden=0
            column frontmatter TEXT notnull=0 default= pk=0 hidden=0
            column parse_error TEXT notnull=0 default= pk=0 hidden=0
            index ix_files_path unique=1 partial=0 (path)
            """.ReplaceLineEndings("\n"),
            Describe(connection, "files"));
    }

    [Fact]
    public void The_scripts_enforce_the_kind_check()
    {
        using var database = new TestDatabase();
        using var connection = database.Open();

        var ex = Assert.Throws<SqliteException>(() => connection.Execute(
            "INSERT INTO files (path, mtime, size, hash, hashed_at, kind) VALUES ('a', 0, 0, 'h', 0, 'other')"));

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

    /// <summary>Columns, indexes and foreign keys, as SQLite reports them, in a form that compares as text.</summary>
    private static string Describe(SqliteConnection connection, string table)
    {
        var lines = new List<string>();
        using (var reader = connection.ExecuteReader($"SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('{table}') ORDER BY cid"))
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
        return lines.Count == 0 ? $"table {table} missing" : string.Join('\n', lines);
    }
}
