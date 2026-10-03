using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hippo.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class MatchPathsUnderNfd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_links_target",
                table: "links");

            migrationBuilder.AddColumn<string>(
                name: "target_nfd",
                table: "links",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_nfd",
                table: "index_entries",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "path_nfd",
                table: "files",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // SQLite cannot decompose, so each row starts out as written, which is already right for a path with
            // nothing to decompose. The schema change makes the next sweep re-read every file and set the rest.
            migrationBuilder.Sql("UPDATE files SET path_nfd = path;");
            migrationBuilder.Sql("UPDATE links SET target_nfd = target;");
            migrationBuilder.Sql("UPDATE index_entries SET target_nfd = target;");

            migrationBuilder.CreateIndex(
                name: "ix_links_target_nfd",
                table: "links",
                column: "target_nfd");

            migrationBuilder.CreateIndex(
                name: "ix_files_path_nfd",
                table: "files",
                column: "path_nfd");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_links_target_nfd",
                table: "links");

            migrationBuilder.DropIndex(
                name: "ix_files_path_nfd",
                table: "files");

            migrationBuilder.DropColumn(
                name: "target_nfd",
                table: "links");

            migrationBuilder.DropColumn(
                name: "target_nfd",
                table: "index_entries");

            migrationBuilder.DropColumn(
                name: "path_nfd",
                table: "files");

            migrationBuilder.CreateIndex(
                name: "ix_links_target",
                table: "links",
                column: "target");
        }
    }
}
