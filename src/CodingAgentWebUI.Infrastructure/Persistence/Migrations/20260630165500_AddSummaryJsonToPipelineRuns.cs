using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaryJsonToPipelineRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SummaryJson was added to the entity model and retroactively inserted into
            // InitialCreate, but the running database was created before that edit.
            // This migration adds the missing column to existing databases.
            migrationBuilder.AddColumn<string>(
                name: "SummaryJson",
                table: "PipelineRuns",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummaryJson",
                table: "PipelineRuns");
        }
    }
}
