using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Drops the <c>NeedsLabelReconciliation</c> column from <c>WorkItems</c>.
    ///
    /// This column was introduced in migration 20260723100000 as infrastructure for a
    /// planned reconciliation sweep that would retry failed label swaps. The consuming sweep
    /// was never implemented, making the column write-only with no observable production effect.
    /// <c>LabelSwapService</c> wrote to it in two places; no production code ever read it.
    ///
    /// Removing the column, the property from <c>WorkItemEntity</c>, and the associated
    /// <c>FlagForLabelReconciliationAsync</c> method + <c>IDbContextFactory</c> dependency
    /// from <c>LabelSwapService</c> eliminates the dead code path entirely.
    ///
    /// <c>Down()</c> re-adds the column as nullable-false with default false, restoring the
    /// schema without data loss should rollback be needed.
    /// </summary>
    public partial class RemoveNeedsLabelReconciliationFromWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsLabelReconciliation",
                table: "WorkItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsLabelReconciliation",
                table: "WorkItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
