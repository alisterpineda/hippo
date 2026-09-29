using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
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
                    hashed_at = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    frontmatter = table.Column<string>(type: "TEXT", nullable: true),
                    parse_error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.id);
                    table.CheckConstraint("ck_files_kind", "kind IN ('markdown', 'plain')");
                });

            migrationBuilder.CreateTable(
                name: "meta",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meta", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "links",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    source_id = table.Column<long>(type: "INTEGER", nullable: false),
                    line = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    raw = table.Column<string>(type: "TEXT", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_links", x => x.id);
                    table.CheckConstraint("ck_links_kind", "kind IN ('body', 'frontmatter')");
                    table.CheckConstraint("ck_links_target", "type = 'path' OR target IS NULL");
                    table.CheckConstraint("ck_links_type", "type IN ('path', 'url', 'anchor')");
                    table.ForeignKey(
                        name: "FK_links_files_source_id",
                        column: x => x.source_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_files_path",
                table: "files",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_links_source_id",
                table: "links",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_links_target",
                table: "links",
                column: "target");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "links");

            migrationBuilder.DropTable(
                name: "meta");

            migrationBuilder.DropTable(
                name: "files");
        }
    }
}
