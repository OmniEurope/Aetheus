using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertOccurrences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertRuleId = table.Column<int>(type: "integer", nullable: true),
                    RuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ServerId = table.Column<int>(type: "integer", nullable: true),
                    ServerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Metric = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Operator = table.Column<int>(type: "integer", nullable: true),
                    Threshold = table.Column<double>(type: "double precision", nullable: true),
                    ObservedValue = table.Column<double>(type: "double precision", nullable: true),
                    SustainedSeconds = table.Column<int>(type: "integer", nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertOccurrences_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AlertOccurrences_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertOccurrences_AlertRuleId",
                table: "AlertOccurrences",
                column: "AlertRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertOccurrences_ServerId",
                table: "AlertOccurrences",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertOccurrences_TriggeredAt",
                table: "AlertOccurrences",
                column: "TriggeredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertOccurrences");
        }
    }
}
