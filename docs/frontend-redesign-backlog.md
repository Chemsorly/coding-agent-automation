# Frontend Redesign — Gap Backlog

Companion to `docs/frontend-redesign-handoff.md`. The Target-IA shell + all 10 screens are built,
wired to real API clients, light + dark. This tracks the gaps between the **built first cut** and the
**canvas spec** — the increments that were deferred or scoped down (nothing here was faked in the build).

**Tags:** `[FE]` frontend-only, no backend change · `[FE+BE]` needs a small API/backend addition · `[BE]` backend/instrumentation.
**Size:** S (hours) · M (a slice) · L (multi-slice).

Walk the list top-down; P1 needs no backend decisions.

**Status:** ✅ **P1 complete** (all 7 items). P2 next (frontend-only polish); P3 needs backend decisions — pause there.

---

## P1 — Frontend-only, high value (start here)

- [x] **Shared cockpit state service** — `[FE, M]`. Circuit-scoped `CockpitState` (selected project + attention count); the layout writes/reads it. Top-bar **Attention count** now shows (computed from run history; Attention page keeps it fresh). *Done:* `Services/CockpitState.cs`, `Program.cs`, `CockpitLayout`, `Attention.razor`.
- [x] **Pipelines: fold in consolidation triggers + Harness Suggestions** — `[FE, M]`. Embedded `<Consolidation />` into the Pipelines page — carries the per-template Brain-Consolidation / Refactoring-Scan cards (`IConsolidationService`) **and** the global Harness Suggestions section. *Done:* `AgentCoding.razor` (after Manual Dispatch); redundant page header hidden via `.cockpit .consolidation-header`.
- [x] **Overview: "Needs attention" preview** — `[FE, S]`. Compact 3-tile card (Needs refinement / Failed runs / Plans to approve) reusing the Attention aggregation, links to `/attention`. *Done:* `Overview.razor`.
- [x] **Insights: outcomes-over-time chart** — `[FE, M]`. Per-day stacked bars over the last 14 days (Completed/Failed/Cancelled) from the fetched run window. *Done:* `Insights.razor` (`DayBucket` bucketing + bar markup).
- [x] **Run Page: enrich the summary detail** — `[FE, S]`. Added a **links rail** (real PR links only), and a **review-findings breakdown** (Critical/Warning/Suggestion tiles + `CodeReviewAgentsRun` reviewers line — the latter was previously unused). *Deferred to P3 (no field on `PipelineRunSummary` → would be fabrication):* branch link, issue-URL link (P3 "link out to the issue"), quality-gate summary line (P3 `QualityGateReport`). *Done:* `RunPage.razor`, `cockpit.css` (`.cockpit-links-rail`, `.cockpit-finding`).
- [x] **Fleet: agent detail panel** — `[FE, S]`. Click a row to expand a detail grid: hostname, connection ID, registered-at, last heartbeat, busy-since, active job/chat, last-job-completed, disconnected-at, orphan-restored, disabled flag (all real `AgentEntry` fields). `Disabled` agents also get a chip in the status cell. *Done:* `Fleet.razor`, `cockpit.css` (`.fleet-detail-row`).
- [x] **Auto-refresh live screens** — `[FE, S]`. New headless `AutoRefresh` component (owns a `PeriodicTimer` + disposal) drives quiet re-fetches on Overview (10s), Fleet (10s), Runs (12s), Work (10s). Ticks refetch without the loading spinner and keep stale data on a transient error; Runs holds its page + filter, Fleet holds the expanded row. *Done:* `Components/Shared/AutoRefresh.razor` + the four pages. (Agent-hub push instead of polling stays a later option.)

## P2 — Frontend-only, completeness & polish

- [x] **Runs: client-side filter of the loaded page (interim)** — `[FE, S]`. Segmented All / Running / Consolidation / Failed over the loaded page, with an explicit "filtering the current page only" hint and a "(filtered)" pager note so it never reads as a real cross-page filter. *Done:* `Runs.razor` (`RunFilter` + `FilteredItems`), `cockpit.css` (`.cockpit-segmented`).
- [ ] **Overview: loop Start/Stop** — `[FE, S]`. *Recommend skip.* The IA deliberately homed loop control in Pipelines; the Overview hero already shows status + a "Manage →" link there. Adding a second control surface duplicates it. Left unbuilt unless you want it.
- [x] **Retire legacy shell** — `[FE, M]`. `CockpitLayout` is now the app default (`Routes.razor`); `MainLayout` + its dead nav deleted. Its app-wide concerns were **ported into CockpitLayout first** (Faro circuit connect/disconnect + notification flush, `FirstRunBanner`, the global `?`/Esc keyboard help + `ShortcutHelpOverlay`, and the Escape cascade that Pipelines' drawers subscribe to). Pages: **About** migrated (re-shelled, added to Tools nav) — kept for its version/build/CI/stats; **Agent Monitoring** migrated (re-shelled — it still owns cancel-run, which the new screens lack); **Consolidation** route → redirect to `/pipelines` (component stays embedded); **Agent Refinement** (dead "Coming soon") → redirect to `/overview`. Onboarding "Register an Agent" link repointed to Fleet. *Tests:* deleted the retired `MainLayoutComponentTests`; added `CockpitLayoutComponentTests`; **fixed pre-existing Task-2 test debt** (720+ AgentCoding tests were red because the folded-in `<Consolidation />` deps weren't registered, + the "Agent Coding"→"Pipelines" rename) — full unit suite now **2512 passed / 0 failed**. *Follow-up (needs the full stack to run):* the E2E/Playwright suite still assumes the legacy shell — `ConsolidationPageTests` hits `/consolidation` + `.sidebar-badge`, and some page objects assert the old sidebar chrome; those need reworking for the cockpit shell.
- [ ] **Icon + spacing polish** — `[FE, S]`. *Down payment done:* nav icon sizes unified (Tools items 17→18). The rest ("glyphs closer to the canvas", "tighten density where it reads dense") is visual-judgment work — best finished with eyes on the rebuilt instance (or by reading the canvas artboards for exact glyphs) rather than guessing blind.

## P3 — Needs a small backend/API addition (decide together)

- [ ] **Runs: server-side status/RunType filter tabs** — `[FE+BE, M]`. Add a filter param to `GetRunHistoryAsync` + the `/api/pipeline-runs` endpoint; then real tabs with correct pagination.
- [ ] **Project-scope filtering on queries** — `[FE+BE, M]`. Project param on run-history / work-item queries so the top-bar switcher actually scopes the data.
- [ ] **Attention: link out to the issue + blocked-issues section** — `[FE+BE, M]`. Issue URL on the run summary (or a lookup) for "open in GitHub"; dependency-readiness source for the blocked-issues section.
- [ ] **Insights: per-gate failure ranking** — `[FE+BE, M]`. Persist/expose quality-gate outcomes (`QualityGateReport`) so "which gate fails most" is real.
- [ ] **Fleet: credential (PVC) health / expiry** — `[FE+BE, M]`. Surface credential expiry from the agent/credential layer (`NoPvcAvailableException` exists; expiry isn't surfaced).
- [ ] **Run Page: live trace timeline + streaming output** — `[FE+BE, L]`. Live job state via the agent hub (`ActiveJobState` / heartbeat steps), not the post-hoc summary — the canvas's richest element.

## P4 — Bigger builds

- [ ] **Work: provider-issue backlog with readiness + dependency graph** — `[FE, L]`. Reuse `IssueDrawerService` / `DependencyChecker` to render the provider issue backlog as a page (Ready / Blocked-by-#N / Needs-refinement + the dependency graph). PriorityWeight stays out — no backend field.
- [ ] **Knowledge: real impact / usage** — `[BE, L]`. Add per-run knowledge-usage instrumentation (record which brain files enter each run's context → join to the outcome), then replace the placeholder with the real screen.

---

*Done so far:* shell + light/dark theme; Settings, Pipelines, Agent Chat (re-shells); Overview, Runs + Run Page, Fleet, Work, Attention, Insights (new); Knowledge (honest placeholder). All compiling, wired to real clients, verified in the browser.
