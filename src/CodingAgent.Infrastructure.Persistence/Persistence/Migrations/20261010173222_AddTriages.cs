using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTriages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Triages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    KeyProviderConfigId = table.Column<string>(type: "text", nullable: false),
                    KeyIdentifier = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    HasOpenAttempt = table.Column<bool>(type: "boolean", nullable: false),
                    Facts = table.Column<string>(type: "jsonb", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Triages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Triages_HasOpenAttempt",
                table: "Triages",
                column: "HasOpenAttempt",
                filter: "\"HasOpenAttempt\"");

            migrationBuilder.CreateIndex(
                name: "IX_Triages_KeyProviderConfigId_KeyIdentifier",
                table: "Triages",
                columns: new[] { "KeyProviderConfigId", "KeyIdentifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Triages_ProjectId_UpdatedAt",
                table: "Triages",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Triages_UpdatedAt",
                table: "Triages",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Triages");
        }
    }
}
