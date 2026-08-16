using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds two partial indexes to support the per-project DB retention sweeps in
    /// <c>DatabaseMaintenanceService.SweepPipelineRunRetentionAsync</c> and
    /// <c>DatabaseMaintenanceService.SweepWorkItemRetentionAsync</c>.
    ///
    /// Both indexes are scoped to rows eligible for the window-function DELETE query:
    /// <c>ProjectId IS NOT NULL</c> rows only (null-project rows are always exempt).
    ///
    /// <c>WorkItems</c> previously had no index on <c>ProjectId</c> at all; this index is
    /// essential for query performance — without it the window-function query performs a
    /// full sequential scan on every sweep.
    /// </summary>
    public partial class AddRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Partial index on PipelineRuns for the retention sweep.
            // Covers: ProjectId IS NOT NULL AND CompletedAt IS NOT NULL
            // (active runs with CompletedAt IS NULL are not eligible for count-based deletion).
            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns",
                columns: new[] { "ProjectId", "StartedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL");

            // Partial index on WorkItems for the terminal-row retention sweep.
            // Status IN (3,4,5): Succeeded=3, Failed=4, Cancelled=5 (WorkItemStatus enum values —
            // see ⚠️ DB CONTRACT comment in WorkItemStatus.cs; do NOT change these values).
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems",
                columns: new[] { "ProjectId", "CompletedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL AND \"Status\" IN (3, 4, 5) AND \"CompletedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems");
        }
    }
}
