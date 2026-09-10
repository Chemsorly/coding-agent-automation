using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgent.Infrastructure.Persistence.Persistence.Migrations
{
    /// <summary>
    /// Back-fills <c>TimeoutSeconds</c> for legacy <c>WorkItems</c> rows that were created
    /// before issue #2179 (Aug 29 2026), which stored <c>0</c> as the default value.
    /// All zero rows are updated to <c>1800</c> (30 minutes — the value of
    /// <c>PipelineConstants.DefaultAgentTimeout</c>) so that dispatch and reconciliation
    /// loops no longer need a zero-sentinel fallback.
    ///
    /// <para>This migration is idempotent: running it multiple times produces the same result
    /// because the <c>WHERE TimeoutSeconds = 0</c> predicate filters out rows that have
    /// already been updated.</para>
    /// </summary>
    public partial class BackFillWorkItemTimeoutSeconds : Migration
    {
        private const int DefaultAgentTimeoutSeconds = 1800; // 30 minutes, matches PipelineConstants.DefaultAgentTimeout

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Back-fill legacy rows that stored 0 for TimeoutSeconds.
            // WHERE TimeoutSeconds = 0 is idempotent: subsequent runs find 0 rows to update.
            // TODO [WARNING]: The SQL is constructed using C# string interpolation with a compile-time
            // constant (DefaultAgentTimeoutSeconds = 1800). This is safe because the value is a numeric
            // literal with no user-supplied input. If this migration template is ever extended to
            // interpolate non-constant or runtime values, it would introduce SQL injection risk.
            // Prefer migrationBuilder.Sql with a plain string literal or parameterized raw SQL for
            // any future migration that involves non-constant values. (SecurityReviewer [WARNING])
            migrationBuilder.Sql(
                $"""
                UPDATE "WorkItems"
                SET "TimeoutSeconds" = {DefaultAgentTimeoutSeconds}
                WHERE "TimeoutSeconds" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down intentionally does NOT restore zeros: there is no way to distinguish rows
            // that were legitimately set to 1800 from rows that were back-filled by this migration.
            // Rolling back would zero-out ALL rows with TimeoutSeconds = 1800, which is destructive.
            // If rollback is required, re-dispatch affected work items instead.
        }
    }
}
