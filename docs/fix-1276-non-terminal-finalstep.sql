-- Fix for Issue #1276: Correct 27 PipelineRun rows with non-terminal finalStep
-- These rows were persisted via the legacy HeartbeatMonitor path (pre-#1253) which
-- called AddRunToHistoryAsync without first setting CurrentStep to a terminal state.
--
-- All affected rows have CompletedAt set and their WorkItems are in terminal status (Failed),
-- so they should have FinalStep = Failed (17).
--
-- PipelineStep enum values: Completed=16, Failed=17, Cancelled=18
-- The issue text incorrectly states Failed=6; that is actually ReviewingAnalysis.

-- Preview affected rows:
-- SELECT "RunId", "SummaryJson"->>'finalStep' AS current_step, "FinalStep", "CompletedAt"
-- FROM "PipelineRuns"
-- WHERE "SummaryJson"->>'finalStep' NOT IN ('Completed', 'Cancelled', 'Failed')
--   AND "SummaryJson"->>'finalStep' IS NOT NULL
--   AND "CompletedAt" IS NOT NULL;

-- Apply fix:
UPDATE "PipelineRuns"
SET "SummaryJson" = jsonb_set("SummaryJson", '{finalStep}', '"Failed"'),
    "FinalStep" = 17
WHERE "SummaryJson"->>'finalStep' NOT IN ('Completed', 'Cancelled', 'Failed')
  AND "SummaryJson"->>'finalStep' IS NOT NULL
  AND "CompletedAt" IS NOT NULL;
