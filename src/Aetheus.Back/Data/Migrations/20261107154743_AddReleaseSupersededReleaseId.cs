using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseSupersededReleaseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupersededReleaseId",
                table: "Releases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Releases_SupersededReleaseId",
                table: "Releases",
                column: "SupersededReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_Releases_Releases_SupersededReleaseId",
                table: "Releases",
                column: "SupersededReleaseId",
                principalTable: "Releases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Releases_Releases_SupersededReleaseId",
                table: "Releases");

            migrationBuilder.DropIndex(
                name: "IX_Releases_SupersededReleaseId",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "SupersededReleaseId",
                table: "Releases");
        }
    }
}
