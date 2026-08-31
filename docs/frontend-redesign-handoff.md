# Frontend Redesign — Build Handoff Brief

**What this is.** The redesign lives as a visual spec on the Claude Design canvas ("Coding Agent Redesign", Target IA page + interactive prototype). This document is its companion: for each screen it names the **real backend** it binds to — API client, method, DTO — and the **existing Blazor components** whose logic can be ported. It has been fact-checked against the current codebase; the backend is **not** changing, so build against exactly what is listed here.

**How to read it.** The new frontend is mostly a *re-shell*: the current app already has working components for most of this data. The redesign moves them into a new nav shell and splits/merges a few screens. Where a screen is genuinely new (Overview, Attention, the Runs *list*, Fleet split out, Insights, Knowledge) that's called out. Anything under "Needs new backend" must not be built as if the data exists.

---

## Global conventions (apply to every screen)

| Concern | Real source of truth | Rule for the UI |
|---|---|---|
| **Run status** | `WorkItemStatus` {Pending, Dispatched, Running, Succeeded, Failed, Cancelled}; finished runs render from `PipelineRunSummary.FinalStep` → **Completed / Failed / Cancelled** | Active runs show a current step; do **not** invent "PR Created / Needs Refinement / Merged / Queued" statuses. |
| **Run type** | `PipelineRunType` {Implementation, Review, DecompositionAnalysis, Decomposition, Consolidation} | Badge accordingly; consolidation runs are a run type and belong in the Runs list. |
| **Cost vs tokens** | `PipelineRunSummary.TotalTokens` (always), `TotalCost` (`decimal?` — **null for Kiro**, set for OpenCode), `CacheReadTokens`/`CacheWriteTokens` | Lead with **tokens**. Show `$` only where present; treat cost as partial (OpenCode-anchored). |
| **Agent status** | `AgentStatus` {Idle, Busy, Disconnected} on `AgentEntry` | Never "Offline". |
| **Health footer** | `IPipelineApiHealthClient.IsHealthyAsync/IsReadyAsync`, `ISchedulerApiClient` | Drives "Scheduler & DB healthy". |
| **Auth** | Operator (master) key required for agent/config endpoints; per-pod derived keys are rejected 403 | The operator UI holds the master key. |

The UI talks to the backend through the **typed API clients** in `CodingAgentWebUI.Api.Client` (inject the `IPipelineApi*Client` interface, never raw HTTP). All DTOs/models live in `CodingAgentWebUI.Pipeline.Models`.

---

## Shared shell (every screen)

- **Left nav** — static destinations: Overview, Work, Runs, Fleet, Attention, Insights, Pipelines, Settings; Tools group: Knowledge, Agent Chat.
- **Top-bar project switcher** — `IPipelineApiConfigClient.GetProjectsAsync()` → `PipelineProject[]` (has `Id`, `Name`, `Enabled`, `TemplateIds`, `EpicIssueProviderId`; default = `WellKnownIds.DefaultProjectId`). Selection scopes the page; "All projects" is the unscoped view.
- **Attention indicator (badge + "N need attention")** — aggregate count for the Attention screen (see below). No single endpoint; compute from its four sources.
- **Health footer** — `IPipelineApiHealthClient` + `ISchedulerApiClient`.

---

## Per-screen mapping

### Overview  ·  *new screen*
Cockpit landing: loop status, stat strip, needs-attention preview, active runs, recent activity.

- **Loop status / stat strip** — the closed-loop controller (`LoopService` injected in `AgentCoding.razor`: `IsLoopActive`, `CurrentCycleTemplateIndex`/`Count`, `ProcessedCount`, `FailedCount`, `StartLoop`/`StopLoop`).
- **Active / Queue** — `IPipelineApiWorkItemClient.GetActiveAsync(...)` → `ActiveWorkItemDto[]`, `GetPendingAsync(maxResults)` → `PendingWorkItemDto[]`.
- **Agents (N/M, offline count)** — `IPipelineApiAgentClient.GetAgentsAsync()` → `AgentEntry[]`.
- **Runs 24h / Success / Tokens** — `IPipelineApiRunHistoryClient.GetRunHistoryAsync(page, pageSize, includeActive:true)` → `PagedResult<PipelineRunSummary>`, aggregated client-side.
- **Recent activity** — same run-history call; render `RunType` + result + `TotalTokens` + `PullRequestUrl`.
- **Reuse:** `AgentMonitoringPageService`, `ActiveRunsSection`, `JobQueueSection`, `RecentRunsSection`.

### Work  ·  *restyle of the dispatch drawers as a full page*
Issue/epic backlog with readiness, labels, epic expansion, dependency graph.

- **Backlog + readiness** — `IIssueDrawerService` / `IssueDrawerService`: `DrawerReadiness` is a `Dictionary<string, DependencyCheckResult>`. `DependencyCheckResult` = `{ IsReady, BlockedBy: int[], TotalDependencies }`, computed by `DependencyChecker` which parses the **issue body** for references (`DependencyParser`) and checks each via `IIssueProvider.IsIssueClosedAsync`. → `Ready` / `Blocked · #N`.
- **Pending queue** — `IPipelineApiWorkItemClient.GetPendingAsync` → `PendingWorkItemDto[]`.
- **Dispatch action** — the drawer dispatch path (`IssueDrawerService`; blocked issues are refused: "Cannot dispatch — blocked by open dependencies").
- **Needs-refinement state** — from `PipelineRunSummary.AnalysisRecommendation` (`AnalysisGateResult?`) / the confidence gate; "Refine ↗" links out to the provider.
- **Reuse:** `IssueDispatchDrawer`, `EpicDispatchDrawer`, `PrDispatchDrawer`, `LabelPreviewInline`.
- **Dropped:** per-issue **PriorityWeight** — no backend field exists. The design uses **Labels** and sorts by recency instead.

### Runs (list)  ·  *new — the index behind the Run Page*
- **Data** — `IPipelineApiRunHistoryClient.GetRunHistoryAsync(page, pageSize, feedbackOnly, includeActive)` → `PagedResult<PipelineRunSummary>` (server-side paging). Filter tabs by status/`RunType`.
- **Columns** — result (Completed/Failed/Cancelled or Running+step), `RunType`, duration (`StartedAtOffset`→`CompletedAtOffset`), `TotalTokens`, `PullRequestUrl`.
- **Consolidation rows** — consolidation runs are `PipelineRunType.Consolidation`; also queryable via `IConsolidationService.GetRunHistoryAsync` / `IPipelineApiConsolidationRunClient.LoadAllRunsAsync` → `ConsolidationRun[]`.
- **Reuse:** `RecentRunsSection` (row rendering), `RunHistoryStats`.

### Run Page  ·  *new — deep-linkable run detail*
- **Data** — `IPipelineApiRunHistoryClient.GetRunAsync(Guid runId)` → `PipelineRunSummary`.
- **Fields (all real on the summary):** trace/step from `FinalStep`+`PipelineStep`/`StepOrder`; `PhaseBreakdown` (per-phase token/cost); `TotalTokens`, `TotalCost` (null for Kiro), `CacheRead/WriteTokens`; quality gates from `QualityGateReport`; review findings `CodeReviewCritical/Warning/SuggestionCount`, `CodeReviewAgentsRun`; `FailureReason`; `RetryCount`; `InitiatedBy`; `BrainRepoUsed`/`BrainUpdatesPushed`; `ModelName`; `AgentId`; PR/branch/issue links.
- **Reuse:** `HistoryRunDetailModal` (same fields, currently a modal — promote to a page).
- **Note:** quality gates are Build / Tests / Coverage (+ external CI); there is **no** "Security" gate (security is a review-agent concern).

### Fleet  ·  *split out of Monitoring*
- **Agents table** — `IPipelineApiAgentClient.GetAgentsAsync()` → `AgentEntry[]` (`Hostname`, `Labels`, `Status`, `ActiveJobId`, `RegisteredAt`, `LastHeartbeatAt`, `DisconnectedAt`, `BusySince`).
- **Utilization / counts** — derived from statuses.
- **Reuse:** `AgentHealthCard`, the agents section of `AgentMonitoring`.
- **Needs new backend:** per-agent credential-*expiry* countdown. PVC credentials are real (`NoPvcAvailableException` = "No agent credentials available"), but expiry is not surfaced today — mark as proposed.

### Attention  ·  *new — aggregation, links out*
Four sections, each from a real source; every row links to the provider:
- **Needs refinement** — runs with `AnalysisRecommendation` = needs-refinement (confidence gate).
- **Epic plans awaiting approval** — decomposition runs whose plan is posted; approve via the `agent:epic-approved` label on the provider.
- **Failed after retries** — `PipelineRunSummary` where result = Failed and `RetryCount` exhausted (`FailureReason` for the sub-line); draft PR via `PullRequestUrl`.
- **Blocked issues** — `DependencyCheckResult.BlockedBy` (from Work's readiness check).
- **Reuse:** none single; compose from run-history + work-item clients.

### Insights  ·  *new — trends*
- **Run outcomes over time / success rate / cycle time / retry rate** — aggregate `IPipelineApiRunHistoryClient.GetRunHistoryAsync` over a window; outcomes are **Completed / Failed / Cancelled** only.
- **Which gate fails most** — Build / Tests / Coverage / External CI, from `QualityGateReport` per failed run.
- **Cost** — `PipelineRunSummary.TotalCost`; show coverage honestly ("OpenCode only — Kiro runs report tokens"). This matches `TotalCost` being nullable.

### Pipelines  ·  *re-shell of Agent Coding*
- **Templates** — `IPipelineApiConfigClient.GetAllTemplatesAsync` / `GetTemplatesForProjectAsync` → `PipelineJobTemplate[]`; save/delete/move via the same client. Columns: Issue/Repo/Brain/CI providers + Features (Impl / Review / Decomposition / Housekeeping / Branch Cleanup toggles) + Enabled + polling Status.
- **Loop control** — `LoopService` (Start/Stop, polling status, circuit-broken → Resume, `ValidationErrors`).
- **Manual dispatch** — select template → Browse Issues / Epics / Pull Requests (the three dispatch drawers).
- **Consolidation triggers** — `IConsolidationService`: `GetLastRunAsync(type, templateId)`, `TriggerAsync(type, templateId, ct, autoDispatch)`, `GetRunHistoryAsync`. Types: `BrainConsolidation`, `RefactoringDetection` (UI label "Refactoring Scan"), `HarnessSuggestions`. **Manual per-template triggers with last-run status — no schedule/cadence, no enable toggle.** Refactoring scan has a pre-flight modal (max issues, hotspot lookback days, adversarial review, optional `agent:next` auto-dispatch) sourced from `PipelineConfiguration`.
- **Harness Suggestions** (global) — `IPipelineApiHarnessSuggestionClient.GetAsync` → `HarnessSuggestions` (`GeneratedAtUtc`, `BasedOnRunCount`, `SuccessRate`, `Suggestions[]`).
- **Reuse:** `AgentCoding.razor`, `TemplateTableSection`, `TemplateAddForm`, `OnboardingChecklist`, `Consolidation.razor`, `ConsolidationBadgeService`.

### Settings  ·  *re-shell of the settings tree*
- **Tree** — `SettingsTreeNav`: Providers (Issue/Repository/Agent/Pipeline) · Projects (Manage + list) · Global Defaults (General, Pipeline Loop, Prompts, Decomposition, Implementation, Review, Consolidation, Advanced) · Label Routing (Agent Profiles, Quality Gate Configs, Reviewer Configs) · Data Management (Import/Export).
- **Quality Gate Configs** — `IPipelineApiConfigClient.GetQualityGateConfigsAsync/SaveQualityGateConfigAsync/DeleteQualityGateConfigAsync` → `QualityGateConfiguration` = `{ DisplayName, MatchLabels[], CompilationCommand + CompilationArguments[], TestCommand + TestArguments[], CoverageThreshold?, ExecutionOrder, ProcessTimeoutSeconds, CoverageReportFormat, CoverageReportPaths[], Enabled }`. It is a **table of command-based configs per tech stack** (empty MatchLabels = global), edited via a form with the command fields — **not** on/off gate toggles.
- **Everything else in the tree** — same `IPipelineApiConfigClient` (provider configs by `ProviderKind`, agent profiles, reviewer configs, projects, templates, import/export, models).
- **Reuse (all exist):** `SettingsTreeNav`, `QualityGateConfigSection`, `ReviewerConfigSection`, `AgentProfileSection`, `ProjectsListSection`, `ProjectDetailSection`, the `Pipeline*Section` set, the provider `*Section` components, `ConfigImportExportSection`, `SettingsModals`.

### Agent Chat  ·  *kept, demoted to Tools*
Launch-a-pod interactive chat — **not** a run debugger.
- **Launch** — `IPipelineApiChatClient.DispatchChatPodAsync(agentSelector, model, effort)` → `agentId` (blocks until pod connects; 409 already active, 503 no PVC, 504 timeout). Selector = a Job Template's labels; model/effort resolved via `ProfileResolver` over `AgentProfile[]`.
- **Send prompt** — build with `IChatPromptBuilder`, deliver via `IPipelineApiAgentClient.AssignChatPromptAsync(agentId, ChatPromptMessage)`.
- **Streaming** — `IAgentHubConnection` (SignalR): `SubscribeToChatSession`, `HubMethodNames.OnChatResponse` / `OnChatCompleted`. Roles: `ChatRole` {User, Agent, System}.
- **Lifecycle** — `TerminateChatSessionAsync`, `SendKeepaliveAsync` (30s heartbeat).
- **Reuse:** `AgentChat.razor` (the flow already exists — re-shell it).

### Knowledge  ·  *concept — NEEDS NEW BACKEND*
The brain is real (`ProviderKind.Brain`; brain repo synced pre/post run via `SyncingBrainRepo*` steps; `PipelineRunSummary.BrainRepoUsed`/`BrainUpdatesPushed`; consolidation writes it). But the screen's **impact / success-lift / per-file usage** metrics require **per-run knowledge-usage instrumentation that does not exist**. Do **not** build this as a data-backed screen. It ships on the canvas marked "Sample data" with an in-artboard note. If pursued, scope the instrumentation first (record which brain files entered each run's context + join to outcome).

---

## Needs new backend (do not build as data-backed)

| Item | Screen | Status |
|---|---|---|
| Knowledge impact/usage metrics | Knowledge | Requires per-run brain-usage instrumentation. Not present. |
| Per-agent credential-expiry countdown | Fleet | PVC credentials exist; expiry not surfaced. Proposed. |
| Per-issue PriorityWeight | Work | No field. **Removed** from the design (uses Labels instead). |

---

## Suggested build order

1. **Shared shell** — nav + top-bar project switcher (`GetProjectsAsync`) + health footer + attention badge scaffold. Everything else mounts inside it.
2. **Settings** — highest reuse (whole tree + `QualityGateConfigSection` already exist); validates the shell against real config clients.
3. **Runs + Run Page** — one client (`IPipelineApiRunHistoryClient`), reuses `RecentRunsSection` + `HistoryRunDetailModal`.
4. **Pipelines** — re-shell `AgentCoding.razor` + `Consolidation.razor`.
5. **Fleet, Overview, Work, Attention, Insights** — the aggregation/new screens, in that order.
6. **Agent Chat** — re-shell `AgentChat.razor`.
7. **Knowledge** — only after its instrumentation is scoped.
