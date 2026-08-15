using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds two partial indexes to support the per-project count-based DB retention sweep
    /// introduced in DatabaseMaintenanceService.
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <c>IX_PipelineRuns_ProjectId_StartedAt</c> — covers the window-function DELETE that
    ///     prunes old PipelineRuns rows beyond the configured per-project retention count.
    ///     Filtered to <c>ProjectId IS NOT NULL</c> so consolidation runs (ProjectId=NULL)
    ///     are never touched and are excluded from the index.
    ///   </item>
    ///   <item>
    ///     <c>IX_WorkItems_ProjectId_CompletedAt_Terminal</c> — covers the window-function DELETE
    ///     that prunes old terminal WorkItems rows beyond the configured per-project retention count.
    ///     Filtered to terminal statuses (Succeeded=3, Failed=4, Cancelled=5) with CompletedAt
    ///     IS NOT NULL. The Status ordinals MUST match the <c>WorkItemStatus</c> enum declaration
    ///     order — see WorkItems unique-constraint filter for the canonical ordinal reference.
    ///   </item>
    /// </list>
    ///
    /// Both indexes can be created concurrently in production (CONCURRENTLY keyword) without
    /// locking the table; the EF migration builder creates them non-concurrently, which is
    /// acceptable for deployment-time migrations.
    /// </summary>
    public partial class AddRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ProjectId_StartedAt",
                table: "PipelineRuns",
                columns: new[] { "ProjectId", "StartedAt" },
                descending: new[] { false, true },
                filter: "\"ProjectId\" IS NOT NULL");

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
