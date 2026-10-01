using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "findings",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    file_id = table.Column<long>(type: "INTEGER", nullable: false),
                    rule = table.Column<string>(type: "TEXT", nullable: false),
                    line = table.Column<long>(type: "INTEGER", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: false),
                    related = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_findings", x => x.id);
                    table.ForeignKey(
                        name: "FK_findings_files_file_id",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_findings_file_id",
                table: "findings",
                column: "file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "findings");
        }
    }
}
