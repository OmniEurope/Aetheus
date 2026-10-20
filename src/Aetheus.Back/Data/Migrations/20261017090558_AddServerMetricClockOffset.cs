using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerMetricClockOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClockOffsetMs",
                table: "ServerMetrics",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClockOffsetUncertaintyMs",
                table: "ServerMetrics",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockOffsetMs",
                table: "ServerMetrics");

            migrationBuilder.DropColumn(
                name: "ClockOffsetUncertaintyMs",
                table: "ServerMetrics");
        }
    }
}
