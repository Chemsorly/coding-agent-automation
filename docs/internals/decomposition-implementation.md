# Epic Decomposition — Implementation Details

Internal reference for the decomposition pipeline step implementations.

## Dispatch Routing

Two `PipelineRunType` values route to dedicated step pipelines:
- `DecompositionAnalysis` → Phase 1 steps
- `Decomposition` → Phase 2 steps

Dispatch chain: `PipelineLoopService` → `DispatchScheduler` → `DispatchOrchestrationService` → `KubernetesWorkDistributor` (`POST /api/work-items`, Pending WorkItem) → Scheduler `WorkItemDispatchLoop` (`POST /api/work-items/{id}/dispatch`) → Pipeline API `DispatchLifecycleService` (K8s Job) → agent pod → `LocalPipelineExecutor`

## Eligibility Filters

When polling for decomposition candidates:

- **Phase 1 (`agent:epic`)**: Skip issues that also carry `agent:epic-review`, `agent:in-progress`, `agent:error`, or `agent:done`
- **Phase 2 (`agent:epic-approved`)**: Skip issues that also carry `agent:in-progress`, `agent:error`, or `agent:done`
- Issues that already have an active WorkItem, or were dispatched earlier in the same cycle, are skipped (`DispatchScheduler.IsIssueAlreadyActive`)

## Loop Integration

Fixed priority order: PRs first, then Decomposition, then Issues (Implementation):
```
Priority 1 (highest): PR queue → dispatched first each cycle
Priority 2:           Decomposition queue (repo epics + project epics share this tier)
Priority 3:           Issue queue (Implementation)
```

- Total dispatches per cycle ≤ `ClosedLoopMaxRunsPerCycle`
- `MinIssueSlots` floor reservation: if Issues were not dispatched in the priority loop, up to `MinIssueSlots` issue slots are reserved in a floor pass, ensuring implementation work is never fully starved
- `MaxConcurrentDecompositions` is enforced from the count of decomposition WorkItems that are Pending, Dispatched or Running (`GET /api/work-items/active-decomposition-count`, loaded at cycle start and increased by each decomposition dispatched in the cycle)
- Repo epics and project epics share the same decomposition queue (the project's executor template holds its project epics alongside its own repo epics); there is no separate sub-priority between them

## Phase 1 Step Details

### WriteOpenIssueContextStep

Downloads open issues via `IAgentIssueOperations` (proxied through SignalR) and writes markdown files to `.agent/open-issues/{identifier}.md`. Each file contains YAML front-matter (identifier, title, labels) followed by the issue body.

Cap: `MaxOpenIssuesForContext` (default 50). Individual fetch failures logged and skipped.

### DecompositionAnalysisStep

1. Writes epic issue body + all comments to `.agent/issue-context.md`
2. Builds analysis prompt with `MaxDecompositionSubIssues` cap
3. Executes agent — expects `.agent/decomposition-plan.md` output
4. Validates plan file exists and has ≥20 characters
5. Executes adversarial review via `AdversarialReviewHelper.ExecuteReviewAsync`
6. On success → `StepResult.Continue`; on failure → `StepResult.Stop`

Adversarial review validates: no overlap with open issues, each sub-issue is right-sized (creates or modifies at most `MaxDecompositionSubIssueFiles` files, default 12; one verification criterion; one agent run), dependencies are acyclic, all epic acceptance criteria are covered, no duplicate titles, and the plan has at most `MaxDecompositionSubIssues` sub-issues.

### PostDecompositionPlanStep

1. Reads `.agent/decomposition-plan.md` from workspace
2. Formats comment with `<!-- agent:decomposition-plan -->` marker + approval instructions
3. Checks for existing plan comment via `ListCommentsAsync` + marker search (most recent match)
4. If existing: updates via `UpdateCommentAsync`; if not: posts new via `PostCommentAsync`
5. Swaps label to `agent:epic-review`

## Phase 2 Step Details

### DecompositionStep

1. Validates plan comment exists (marker detection); fails the run if the epic has no plan comment
2. Writes epic body + all comments (including plan comment) to `.agent/issue-context.md`
3. Queries existing `agent:generated` sub-issues for deduplication context
4. Builds decomposition prompt with `MaxDecompositionSubIssues` cap
5. Executes agent — expects `.agent/sub-issues/*.json` output

### CreateSubIssuesStep

1. Parses sub-issue files via `SubIssueFileParser` (alphabetical order)
2. Enforces `MaxDecompositionSubIssues` cap (takes first N alphabetically)
3. Creates issues sequentially via `IAgentIssueOperations.CreateIssueAsync`, or `CreateIssueForProviderAsync` when a project epic's sub-issue is routed to another template's tracker
4. For each issue: sanitizes title (`TextSanitizer.SanitizeTitle`), sanitizes body (`TextSanitizer.SanitizeMarkdown`), resolves dependencies via `DependencyResolver`
5. Applies labels: `agent:next` + `agent:generated` + custom labels from JSON
6. Retries transient errors (3 attempts, exponential backoff: 0s, 1s, 3s)
7. 5-minute creation phase timeout — remaining issues marked as failed on timeout
8. Tracks results in `List<SubIssueCreationResult>` for the summary step

### PostDecompositionSummaryStep

1. Reads `SubIssueResults` from the pipeline run
2. Formats summary comment with `<!-- agent:decomposition-summary -->` marker
3. Posts via `PostCommentAsync`
4. All succeeded → `agent:done`; all failed → `agent:error`; partial → `agent:done`
5. Summary post failure → log error, proceed with label swap

## Sub-Issue JSON Schema

Agent writes sub-issue files to `.agent/sub-issues/` as JSON.

### File Naming Convention

```
{NN}-{title-slug}.json
```

- `{NN}` — Zero-padded two-digit sequence starting at `01`
- `{title-slug}` — Lowercase, hyphen-separated slug (max 60 chars)

### Schema

```json
{
  "title": "Add user authentication endpoint",
  "body": "## Summary\n\n...\n\n## Acceptance Criteria\n\n- [ ] ...",
  "dependencies": ["Create database schema"],
  "labels": ["enhancement"]
}
```

### Field Reference

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `title` | `string` | Yes | Non-empty, max 256 characters |
| `body` | `string` | Yes | Non-empty, markdown. Must contain: Summary, Affected Components, Requirements, Acceptance Criteria |
| `dependencies` | `string[]` | Yes | Title references to other sub-issues (resolved to `#N` during creation) |
| `labels` | `string[]` | Yes | Additional labels beyond auto-applied `agent:next` and `agent:generated` |
| `targetRepository` | `string` | No | Template name whose tracker receives the sub-issue (project epics only); an unknown or empty value sends it to the executor template's tracker |

### Validation Rules

- Valid UTF-8 JSON without BOM
- The four fields above `targetRepository` are required with correct types
- Invalid files logged and skipped

### Dependency Resolution

1. Creates issues in alphabetical file-name order
2. Maintains title→issue-number mapping
3. Resolves titles case-insensitively (whitespace-trimmed): a dependency created in the same tracker becomes `#N`, one created in another tracker becomes its full issue URL
4. Inserts the `Depends on #N` or `Depends on <url>` lines at the top of the body before creation
5. Unresolved titles (including forward references) logged as warnings and omitted

## Workspace Conventions

### Path

Decomposition runs use the same workspace path as all other run types:

```
{WorkspaceBaseDirectory}/{runId}/
```

There is no `decomposition/` subdirectory. Consolidation runs work the same way: the agent uses `{WorkspaceBaseDirectory}/{jobId}/`, with the work item id as the job id.

### Agent Workspace Paths

| Path | Purpose |
|------|---------|
| `.agent/open-issues/` | Open issue context files for deduplication |
| `.agent/sub-issues/` | Sub-issue JSON output files |
| `.agent/decomposition-plan.md` | Decomposition plan output (Phase 1) |
| `.agent/decomposition-review.md` | Adversarial review findings (Phase 1) |
| `.agent/issue-context.md` | Epic body + comments context |

All paths defined as constants in `AgentWorkspacePaths`.

### Cleanup

- **Success**: Workspace deleted recursively
- **Failure**: Kept until the agent pod ends; the workspace lives on the pod's disk
- **Deletion failure**: Logged as warning, execution continues

## Comment Markers

| Marker | Purpose |
|--------|---------|
| `<!-- agent:decomposition-plan -->` | Identifies plan comment (first line) |
| `<!-- agent:decomposition-summary -->` | Identifies summary comment |

Markers enable idempotent updates on re-run.
