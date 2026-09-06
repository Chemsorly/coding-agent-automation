using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHarnessVersionToPipelineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HarnessVersion",
                table: "PipelineRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HarnessVersion",
                table: "PipelineRuns");
        }
    }
}
