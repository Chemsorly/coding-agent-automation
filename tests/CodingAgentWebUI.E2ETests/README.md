# CodingAgentWebUI.E2ETests — Parked for Spec 045

## Status

The E2E CI workflow is **disabled** as of Spec 041 (branch `feature/041-045-kubernetes-refactoring`,
commit `2e0ea03c`). The workflow triggers were reduced to `workflow_dispatch` only in
`.github/workflows/e2e-tests.yml` (Task 2.1).

The project itself **still compiles** and **remains in `CodingAgentAutomation.sln`**. It was not
removed because it builds cleanly against the current tree (Task 2.2 verification).

**Spec 045 owns the rebuild and must re-enable the CI job before the branch merges.**

## Why it is parked

Specs 041–045 ship as a single PR. This spec (041) removes docker-compose and the Legacy /
SignalR work distribution modes. Specs 042–044 then move Postgres behind an API service, extract a
job controller, and re-point agent pods. The process topology changes four times across the arc.

Rebuilding the E2E harness against the 041-only shape would be discarded by 042–044. It is
therefore rebuilt **once**, in Spec 045, against the final four-service architecture.

## What Spec 045 must do (Req 10.4)

The harness currently has four factory base classes:

| Factory | Derives | Test files |
|---|---|---|
| `E2EWebApplicationFactory` | `E2ETestBase` | 24 files — Legacy mode |
| `DbModeE2EWebApplicationFactory` | `DbModeE2ETestBase` | 7 files — DB+SignalR mode |
| `K8sModeE2EWebApplicationFactory` | `K8sModeE2ETestBase` | 1 file |
| `K8sChatE2EWebApplicationFactory` | `K8sChatE2ETestBase` | 1 file |

Spec 045 must:

1. **Collapse all four factories into a single factory** targeting the final architecture. The
   `K8sModeE2EWebApplicationFactory` currently sets `WorkDistribution__Mode=SignalR` to avoid
   `InClusterConfig()`. Spec 041 Req 5.9 removes that need — the new factory can register the
   real K8s stack and stub `IKubernetes`.

2. **Delete `tests/CodingAgentWebUI.E2ETests/Tests/CrossModeParityTests.cs`** rather than
   porting it. With a single deployment mode the parity assertion is meaningless.

3. **Remove `RemoveHostedService<PendingWorkItemDrainService>(services)`** from any factory
   that contains it. `PendingWorkItemDrainService` is deleted by Spec 041 Req 4.1 — the call
   will not compile once the E2E project is brought back into the solution build against the
   post-041 tree.

4. **Restore the project to `CodingAgentAutomation.sln`** (it remains there now, but confirm
   nothing changed).

5. **Re-enable the CI workflow** (`.github/workflows/e2e-tests.yml`) — the branch must not
   merge with the job disabled.

## Do not edit test files in 041–044

Per Spec 041 Req 10.3, no E2E test file is to be edited or deleted in specs 041–044. The files
are preserved intact for Spec 045 to port. The only permitted exception is minimum edits to
restore compilation if the project were removed from and re-added to the solution — which it
was not.
