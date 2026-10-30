using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistryPackageAnonymousDownload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowAnonymousDownload",
                table: "RegistryPackages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // AETHEUS_EXPAND_CONTRACT_SQL_REVIEWED: data-only UPDATE of the column added above; no schema or row removed.
            // R-58 (user decision 2026-10-27): client projects restore the observability packages
            // while building their deploy image, on an agent that carries no token. Only these three
            // packages are opened to anonymous reading; every other package stays private and an
            // administrator can close them again from the package registry page. Kind 0 = NuGet,
            // 1 = npm (PackageRegistryKind); NormalizedName is lower-case for NuGet, verbatim for npm.
            migrationBuilder.Sql(
                """
                UPDATE "RegistryPackages"
                SET "AllowAnonymousDownload" = TRUE
                WHERE ("Kind" = 0 AND "NormalizedName" IN ('aetheus.telemetry', 'aetheus.webanalytics'))
                   OR ("Kind" = 1 AND "NormalizedName" = '@aetheus/web-analytics');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowAnonymousDownload",
                table: "RegistryPackages");
        }
    }
}
