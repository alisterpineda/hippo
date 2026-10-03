using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FTS5 cannot add a column, so the search table is recreated with every row copied across, rowid included,
            // as SearchIndex.UseTokenizer does. Indexes at version 6 have it with or without description (0004 was once
            // edited to add it), so only the columns both have are copied. The schema change makes the next sweep
            // re-read every file and set the description. A table made under trigram comes back under porter, and that
            // sweep turns it back.
            migrationBuilder.Sql("CREATE TEMP TABLE search_copy AS SELECT rowid AS id, title, path, body FROM search;");
            migrationBuilder.Sql("DROP TABLE search;");
            migrationBuilder.Sql("CREATE VIRTUAL TABLE search USING fts5(title, description, path, body, tokenize = 'porter unicode61');");
            migrationBuilder.Sql("INSERT INTO search (rowid, title, description, path, body) SELECT id, title, '', path, body FROM temp.search_copy;");
            migrationBuilder.Sql("DROP TABLE temp.search_copy;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE TEMP TABLE search_copy AS SELECT rowid AS id, title, path, body FROM search;");
            migrationBuilder.Sql("DROP TABLE search;");
            migrationBuilder.Sql("CREATE VIRTUAL TABLE search USING fts5(title, path, body, tokenize = 'porter unicode61');");
            migrationBuilder.Sql("INSERT INTO search (rowid, title, path, body) SELECT id, title, path, body FROM temp.search_copy;");
            migrationBuilder.Sql("DROP TABLE temp.search_copy;");
        }
    }
}
