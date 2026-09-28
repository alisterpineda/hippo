using Dapper;
using Hippo.Indexing;
using Hippo.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hippo.Tests.Unit;

/// <summary>Guards that the embedded SQL scripts stay in step with the EF model they were exported from.</summary>
public class SchemaTests
{
    private static HippoDbContext CreateContext() => new HippoDbContextFactory().CreateDbContext([]);

    [Fact]
    public void Every_migration_has_a_script_and_every_script_a_migration()
    {
        using var context = CreateContext();
        Assert.Equal(context.Database.GetMigrations(), MigrationRunner.Scripts.Select(s => s.MigrationId));
    }

    [Fact]
    public void Scripts_are_numbered_from_1_without_gaps()
    {
        Assert.Equal(Enumerable.Range(1, MigrationRunner.Scripts.Count), MigrationRunner.Scripts.Select(s => s.Version));
    }

    [Fact]
    public void Tables_built_from_the_scripts_match_the_model()
    {
        using var context = CreateContext();
        using var fromScripts = new TestDatabase();
        using var fromModel = new TestDatabase();
        fromScripts.Migrate();
        using (var connection = fromModel.Connect())
        {
            connection.Execute(context.Database.GenerateCreateScript());
        }

        using var scripts = fromScripts.Connect();
        using var model = fromModel.Connect();
        var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct().ToList();
        Assert.NotEmpty(tables);
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
            "INSERT INTO files (path, mtime, size, hash, kind) VALUES ('a', 0, 0, 'h', 'other')"));

        Assert.Contains("CHECK constraint failed", ex.Message);
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
