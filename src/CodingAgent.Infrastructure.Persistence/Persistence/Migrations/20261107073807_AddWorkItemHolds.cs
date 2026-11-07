using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Credential",
                table: "WorkItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeldAt",
                table: "WorkItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldReason",
                table: "WorkItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldText",
                table: "WorkItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Credential",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "HeldAt",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "HoldReason",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "HoldText",
                table: "WorkItems");
        }
    }
}
