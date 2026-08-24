using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Restores the two partial retention indexes dropped by
    /// <c>20260820105151_BranchPendingChanges</c>.
    ///
    /// That migration claimed "the pruning service now uses filtered scans without relying on
    /// these indexes." This is false — <c>DatabaseMaintenanceService.SweepPipelineRunRetentionAsync</c>
    /// and <c>SweepWorkItemRetentionAsync</c> still execute <c>ROW_NUMBER() OVER (PARTITION BY "ProjectId")</c>
    /// window-function DELETEs against the full tables. Without these indexes the Postgres query
    /// planner performs a full sequential scan on every sweep cycle, degrading silently because
    /// the sweep's only error path logs a non-fatal warning.
    ///
    /// Index shapes verified against the current retention queries (2026-08-24):
    /// - PipelineRuns: <c>ProjectId IS NOT NULL AND CompletedAt IS NOT NULL</c>, ordered StartedAt DESC
    /// - WorkItems: <c>ProjectId IS NOT NULL AND Status IN (3,4,5) AND CompletedAt IS NOT NULL</c>, ordered CompletedAt DESC
    /// </summary>
    public partial class RestoreRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId_CompletedAt_Terminal",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns");
        }
    }
}
