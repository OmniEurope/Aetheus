using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCertificateExpiryAlertMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificateExpiryAlertMarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    CertificateName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeadDays = table.Column<int>(type: "integer", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateExpiryAlertMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CertificateExpiryAlertMarks_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateExpiryAlertMarks_ServerId_CertificateName_Expiry~",
                table: "CertificateExpiryAlertMarks",
                columns: new[] { "ServerId", "CertificateName", "ExpiryDate", "LeadDays" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateExpiryAlertMarks");
        }
    }
}
