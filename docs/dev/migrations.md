# Migrations

The schema is authored as EF Core entities in `src/Hippo.Migrations`; hippo itself reads and writes with Dapper and never loads EF. After changing the entities, run:

```sh
scripts/add-migration.sh <Name>
```

It adds the EF migration and exports it alone to `src/Hippo/Migrations/NNNN_<migration id>.sql`, which hippo embeds and applies on start, all pending scripts in one transaction. The number is the schema version, kept in SQLite's `user_version`.

A migration keeps the data in the index:

- EF alters a table in place where SQLite can, and otherwise rebuilds it by copying every row into a new table. Review the SQL. When EF says a change may lose data, the script repeats the warning last; read every `DROP` it wrote.
- A new `NOT NULL` column needs a value for the rows already there. Choose it on purpose and declare it in the model with `HasDefaultValue`; `SchemaTests` fails until the model and the scripts build the same schema. hippo re-reads every file after a migration and rewrites each row in place, keeping its id, so a column derived from the file is filled then.
- `MigrationRunnerTests` migrates a populated index from the first schema through every script and fails when a row, or a value in a column the first schema had, is gone. It does not seed or check columns a later migration adds. A migration meant to discard data changes that test in the same commit.
- A committed script is never edited, even before a release; fixes go forward in a new migration. An index that ran a script never runs it again, so an edit would leave it on the old schema at a version that looks current. `SchemaTests` holds each committed script's checksum and fails when one changes; a new script adds its line. Each index also records a fingerprint of the scripts it ran, and hippo refuses an index whose scripts differ from its own at that version, naming the folder to delete.
- EF cannot model an FTS5 table. The `search` table is created by `migrationBuilder.Sql` in its migration, the model leaves it out, and `SchemaTests` compares only ordinary tables; a change to it is SQL written by hand in a new migration. FTS5 cannot alter a table's columns, so such a migration recreates the table and copies every row across, rowid included, as `AddSearchDescription` does. `SearchIndex.Create`, which recreates the table when the tokenizer changes, must write the same SQL: `SchemaTests` checks that the two agree.
