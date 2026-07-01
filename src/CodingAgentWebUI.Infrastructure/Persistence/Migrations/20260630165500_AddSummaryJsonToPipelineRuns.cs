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
            // Uses IF NOT EXISTS to be idempotent — handles cases where the column
            // was already added via the modified InitialCreate migration.
            migrationBuilder.Sql("""
                ALTER TABLE "PipelineRuns" ADD COLUMN IF NOT EXISTS "SummaryJson" jsonb;
                """);
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
