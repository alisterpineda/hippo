using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Files : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    mtime = table.Column<long>(type: "INTEGER", nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    hash = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    frontmatter = table.Column<string>(type: "TEXT", nullable: true),
                    parse_error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.id);
                    table.CheckConstraint("ck_files_kind", "kind IN ('markdown', 'plain')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_files_path",
                table: "files",
                column: "path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "files");
        }
    }
}
