using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aetheus.Back.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDependencyUpdateMergeRequestFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BranchRetiredAt",
                table: "DependencyUpdates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PullRequestId",
                table: "DependencyUpdates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadyHandledAt",
                table: "DependencyUpdates",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchRetiredAt",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "PullRequestId",
                table: "DependencyUpdates");

            migrationBuilder.DropColumn(
                name: "ReadyHandledAt",
                table: "DependencyUpdates");
        }
    }
}
