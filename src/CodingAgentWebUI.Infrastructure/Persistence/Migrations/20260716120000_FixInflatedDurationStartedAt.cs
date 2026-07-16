using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodingAgentWebUI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Data-fix migration for Issue #1299: Corrects inflated StartedAt values for pipeline runs
    /// affected by the July 14-15 pod restart cascade.
    ///
    /// These runs were cancelled and re-dispatched, but their persisted StartedAt still reflects
    /// the original dispatch time (Jul 14 ~20:08) instead of the re-dispatch time (Jul 15 ~04:24+).
    /// This caused the UI DURATION column to show 3-5 hour values instead of actual 40-120 minutes.
    ///
    /// The fix updates StartedAt (entity column) and SummaryJson.startedAt/startedAtOffset
    /// using WorkItems.DispatchedAt as the authoritative start time.
    ///
    /// Safety guards:
    ///   - Time window: only runs completed Jul 15 04:00-12:00 UTC
    ///   - Duration threshold: only runs showing > 2 hours (7200 seconds)
    ///   - DispatchedAt > StartedAt: confirms re-dispatch occurred
    ///   - Excludes Cancelled runs (their long duration is legitimate)
    ///   - Idempotent: condition is false after correction (DispatchedAt = StartedAt)
    ///
    /// TODO: [Review] The finalStep != 'Cancelled' filter uses SQL three-valued logic — rows with
    ///   NULL SummaryJson or missing 'finalStep' key are silently skipped. Consider using
    ///   IS DISTINCT FROM 'Cancelled' if NULL SummaryJson rows could be affected.
    /// TODO: [Review] The entity column "StartedAt" is set directly from wi."DispatchedAt" without
    ///   explicit UTC conversion. Safe in practice (DispatchedAt is always UTC), but if non-UTC
    ///   offsets were ever stored, the entity column would retain the original offset while JSON
    ///   fields are forced to UTC.
    /// TODO: [Review] No integration test verifies this migration SQL produces correct results.
    ///   A test seeding a PipelineRuns row with inflated StartedAt and matching WorkItems row,
    ///   then applying the migration and asserting corrected values, would catch SQL/JSON errors.
    /// TODO: [Review] No test verifies boundary/negative cases — e.g., that runs completed outside
    ///   the Jul 15 04:00-12:00 window or Cancelled runs within the window are NOT modified.
    /// </summary>
    public partial class FixInflatedDurationStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "PipelineRuns" pr
                SET "StartedAt" = wi."DispatchedAt",
                    "SummaryJson" = jsonb_set(
                        jsonb_set(
                            pr."SummaryJson"::jsonb,
                            '{startedAt}',
                            to_jsonb(to_char(wi."DispatchedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))
                        ),
                        '{startedAtOffset}',
                        to_jsonb(to_char(wi."DispatchedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"+00:00"'))
                    )
                FROM "WorkItems" wi
                WHERE wi."Id" = pr."RunId"
                  AND pr."CompletedAt" BETWEEN '2026-07-15 04:00:00+00' AND '2026-07-15 12:00:00+00'
                  AND EXTRACT(EPOCH FROM (pr."CompletedAt" - pr."StartedAt")) > 7200
                  AND wi."DispatchedAt" IS NOT NULL
                  AND wi."DispatchedAt" > pr."StartedAt"
                  AND pr."SummaryJson"->>'finalStep' != 'Cancelled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-fix migration — Down is intentionally a no-op.
            // The original incorrect values are not preserved; reverting would require
            // a backup or re-introducing the bug. This is a one-time correction.
        }
    }
}
