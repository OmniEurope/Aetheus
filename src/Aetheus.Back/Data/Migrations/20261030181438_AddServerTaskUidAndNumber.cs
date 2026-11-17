using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerTaskUidAndNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "Uid",
                table: "Tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaskCounter",
                table: "Servers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: ADR-060, additive, no row and no column removed. Each counter
            // starts at the number of tasks its server already holds, so the numbers 1 to that count stay free
            // for them (TaskIdentityBackfillService, oldest first) and new tasks continue after them. A task the
            // previous colour inserts after this statement is unaffected: it reads neither the column nor the
            // counter, and the backfill numbers it past the counter.
            migrationBuilder.Sql(
                """
                UPDATE "Servers" AS server
                SET "TaskCounter" = tasks.total
                FROM (
                    SELECT "ServerId", COUNT(*) AS total
                    FROM "Tasks"
                    GROUP BY "ServerId"
                ) AS tasks
                WHERE tasks."ServerId" = server."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ServerId_Number",
                table: "Tasks",
                columns: new[] { "ServerId", "Number" },
                unique: true,
                filter: "\"Number\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Uid",
                table: "Tasks",
                column: "Uid",
                unique: true,
                filter: "\"Uid\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_ServerId_Number",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Uid",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Uid",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "TaskCounter",
                table: "Servers");
        }
    }
}
