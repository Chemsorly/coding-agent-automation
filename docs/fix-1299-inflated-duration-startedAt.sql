-- Fix for Issue #1299: Correct StartedAt for ~12-20 PipelineRun rows affected by
-- the July 14-15 pod restart cascade.
--
-- Problem: These rows have StartedAt = original dispatch time (Jul 14 ~20:08 UTC)
-- instead of re-dispatch time (Jul 15 ~04:24+ UTC), causing the UI DURATION column
-- to display inflated values (3-5 hours instead of actual 40-120 minutes).
--
-- Root cause: Pod restart cancelled running work, then re-dispatched the same
-- WorkItems (same WorkItem.Id = PipelineRun.RunId). The in-memory PipelineRun was
-- reconstructed with the original StartedAt rather than the re-dispatch DispatchedAt.
-- Fixed for new runs by PR #1295 (merged Jul 15 11:54 UTC).
--
-- Join: wi."Id" = pr."RunId" (WorkItem.Id == PipelineRun.RunId, set by ResolveWorkItemId)
-- The WorkItemId column on PipelineRunEntity is not populated for historical runs.
--
-- Safety guards:
--   - Time window: only runs completed Jul 15 04:00-12:00 UTC
--   - Duration threshold: only runs showing > 2 hours (7200 seconds)
--   - DispatchedAt > StartedAt: confirms re-dispatch occurred
--   - Excludes Cancelled runs (their long duration is legitimate)
--   - Idempotent: condition is false after correction (DispatchedAt = StartedAt)

-- ═══════════════════════════════════════════════════════════════════════
-- STEP 1: Preview affected rows (run in a transaction, ROLLBACK after review)
-- ═══════════════════════════════════════════════════════════════════════
-- BEGIN;
--
-- SELECT pr."RunId",
--        pr."StartedAt" AS column_started,
--        pr."SummaryJson"->>'startedAtOffset' AS json_started_offset,
--        pr."CompletedAt",
--        wi."DispatchedAt" AS actual_start,
--        pr."SummaryJson"->>'finalStep' AS final_step,
--        EXTRACT(EPOCH FROM (pr."CompletedAt" - pr."StartedAt"))/60 AS current_duration_min,
--        EXTRACT(EPOCH FROM (pr."CompletedAt" - wi."DispatchedAt"))/60 AS corrected_duration_min
-- FROM "PipelineRuns" pr
-- JOIN "WorkItems" wi ON wi."Id" = pr."RunId"
-- WHERE pr."CompletedAt" BETWEEN '2026-07-15 04:00:00+00' AND '2026-07-15 12:00:00+00'
--   AND EXTRACT(EPOCH FROM (pr."CompletedAt" - pr."StartedAt")) > 7200
--   AND wi."DispatchedAt" IS NOT NULL
--   AND wi."DispatchedAt" > pr."StartedAt"
--   AND pr."SummaryJson"->>'finalStep' != 'Cancelled';
--
-- Expect ~12-20 rows, all with corrected_duration_min < 120
-- ROLLBACK;

-- ═══════════════════════════════════════════════════════════════════════
-- STEP 2: Apply fix
-- ═══════════════════════════════════════════════════════════════════════
UPDATE "PipelineRuns" pr
SET "StartedAt" = wi."DispatchedAt",
    "SummaryJson" = jsonb_set(
        jsonb_set(
            pr."SummaryJson"::jsonb,
            '{startedAt}',
            -- Note: .US format emits 6 fractional digits; System.Text.Json trims trailing
            -- zeros. Cosmetic difference only — deserialization handles both formats.
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

-- ═══════════════════════════════════════════════════════════════════════
-- STEP 3: Post-fix verification
-- ═══════════════════════════════════════════════════════════════════════
-- SELECT pr."RunId",
--        pr."StartedAt",
--        pr."SummaryJson"->>'startedAtOffset' AS json_started_offset,
--        pr."CompletedAt",
--        EXTRACT(EPOCH FROM (pr."CompletedAt" - pr."StartedAt"))/60 AS duration_min
-- FROM "PipelineRuns" pr
-- WHERE pr."CompletedAt" BETWEEN '2026-07-15 04:00:00+00' AND '2026-07-15 12:00:00+00'
--   AND pr."SummaryJson"->>'finalStep' != 'Cancelled'
-- ORDER BY pr."CompletedAt";
-- Verify: all durations < 120 minutes
