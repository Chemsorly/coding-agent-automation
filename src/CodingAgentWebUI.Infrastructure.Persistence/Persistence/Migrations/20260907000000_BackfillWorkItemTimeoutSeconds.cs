using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Data-fix migration for Issue #2405: back-fills <c>TimeoutSeconds = 1800</c> (30-minute default)
    /// for all <c>WorkItems</c> rows where <c>TimeoutSeconds = 0</c>.
    ///
    /// <para>
    /// Pre-#2179 work items were enqueued without a <c>TimeoutSeconds</c> value, so the column
    /// defaulted to 0 in the database. After this migration all rows carry a meaningful positive
    /// value and the zero-sentinel fallback in <c>DispatchLoop</c>, <c>ConsolidationDispatchLoop</c>,
    /// and <c>ReconciliationLoop</c> can be safely removed.
    /// </para>
    ///
    /// Safety / idempotency:
    /// <list type="bullet">
    ///   <item>The <c>WHERE TimeoutSeconds = 0</c> predicate means re-running is harmless — rows
    ///   already at 1800 are unaffected.</item>
    ///   <item>No schema change is performed; only row data is updated.</item>
    /// </list>
    /// </summary>
    public partial class BackfillWorkItemTimeoutSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """UPDATE "WorkItems" SET "TimeoutSeconds" = 1800 WHERE "TimeoutSeconds" = 0;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-fix migration — Down is intentionally a no-op.
            // The original zero values are not preserved; reverting would require a backup.
            // This is a one-time correction and is not meaningfully reversible.
        }
    }
}
