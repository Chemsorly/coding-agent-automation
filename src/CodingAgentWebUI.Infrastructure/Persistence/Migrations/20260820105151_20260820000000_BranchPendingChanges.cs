using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Drops two partial-index retention indexes that were added in the previous migration
    /// (<c>20260816000000_AddRetentionIndexes</c>) but caused query-planner regressions.
    /// The indexes are no longer needed because the pruning service now uses filtered scans
    /// without relying on these indexes. Rolling back (Down) recreates them with their
    /// original partial-index filters.
    /// </summary>
    public partial class _20260820000000_BranchPendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems",
                columns: new[] { "ProjectId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"Status\" IN (3, 4, 5) AND \"CompletedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns",
                columns: new[] { "ProjectId", "StartedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL");
        }
    }
}
