using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Drops the spurious <c>IX_WorkItems_ProjectId</c> single-column index that was created by
    /// the preceding migration (<c>WorkItemProjectIdToUuidWithFk</c>) but is absent from
    /// <c>PipelineDbContext.OnModelCreating</c>.
    ///
    /// Root cause: the raw <c>ALTER COLUMN … TYPE uuid USING …</c> SQL in the previous migration
    /// bypassed EF's automatic FK-index generation, so <c>CreateIndex("IX_WorkItems_ProjectId")</c>
    /// was added manually to keep the schema in sync. However, the matching <c>HasIndex(w =&gt; w.ProjectId)</c>
    /// call was not added to <c>OnModelCreating</c>. EF therefore considers the index untracked,
    /// which causes a <c>PendingModelChangesWarning</c> on every startup and prevents migration
    /// from completing in production (where <c>MigrateOnStartup=false</c>).
    ///
    /// The FK constraint itself (<c>FK_WorkItems_Projects_ProjectId</c>) still exists and is
    /// unaffected by this migration. The compound retention index
    /// <c>IX_WorkItems_ProjectId_CompletedAt_Terminal</c> is also unaffected. Postgres does not
    /// require a single-column index on a FK column — the FK constraint is enforced via the
    /// <c>Projects.Id</c> primary key index on the referenced table.
    /// </summary>
    public partial class DropSpuriousWorkItemProjectIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_ProjectId",
                table: "WorkItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId",
                table: "WorkItems",
                column: "ProjectId");
        }
    }
}
