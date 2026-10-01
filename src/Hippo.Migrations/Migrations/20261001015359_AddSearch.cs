using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF cannot model an FTS5 table, so the model leaves it out and this migration creates it as SQL. Each row's
            // rowid is its page's id in files. The sweep writes it directly, with no triggers, and recreates it when
            // search.tokenizer changes (SearchIndex.cs).
            migrationBuilder.Sql("CREATE VIRTUAL TABLE search USING fts5(title, path, body, tokenize = 'porter unicode61');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE search;");
        }
    }
}
