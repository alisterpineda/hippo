using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "index_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    file_id = table.Column<long>(type: "INTEGER", nullable: false),
                    line = table.Column<long>(type: "INTEGER", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_index_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_index_entries_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_index_entries_file_id",
                table: "index_entries",
                column: "file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "index_entries");
        }
    }
}
