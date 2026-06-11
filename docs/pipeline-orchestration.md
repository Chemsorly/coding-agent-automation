# Pipeline Orchestration

The pipeline is a state machine that progresses through a fixed sequence of steps, with decision points that can branch to terminal states. There are three pipeline workflows:

1. **Implementation pipeline** — Processes issues through analysis, code generation, quality gates, and PR creation
2. **PR review pipeline** — Processes pull requests through code review and posts findings (see [PR Review Pipeline](#pr-review-pipeline) below)
3. **Epic decomposition pipeline** — Processes epics through a two-phase workflow producing implementation-ready sub-issues (see [Epic Decomposition Pipeline](#epic-decomposition-pipeline) below)

All three workflows share the same dispatch mechanism, label lifecycle, and agent infrastructure.

See also: [Configuration](configuration.md) for all pipeline settings, and [Issue Workflows](github-issue-workflows.md) for how users interact with the pipeline via labels.

```mermaid
stateDiagram-v2
    direction TB

    [*] --> Created
    Created --> CloningRepository
    CloningRepository --> RunningEnvironmentSetup
    RunningEnvironmentSetup --> SyncingBrainRepoPreRun
    SyncingBrainRepoPreRun --> CreatingBranch
    CreatingBranch --> VerifyingBaseline
    VerifyingBaseline --> AnalyzingCode
    AnalyzingCode --> ReviewingAnalysis
    ReviewingAnalysis --> PostingAnalysis

    state ConfidenceGate <<choice>>
    PostingAnalysis --> ConfidenceGate
    ConfidenceGate --> GeneratingCode : ready
    ConfidenceGate --> Failed : not_ready (needs-refinement)
    ConfidenceGate --> Completed : wont_do (wont-do)
    GeneratingCode --> ReviewingCode
    ReviewingCode --> RunningQualityGates

    state QualityGateDecision <<choice>>
    RunningQualityGates --> QualityGateDecision
    QualityGateDecision --> PreparingForPullRequest : all passed
    QualityGateDecision --> GeneratingCode : failed, retries remaining
    QualityGateDecision --> CreatingPullRequest : failed, retries exhausted (draft PR)

    state FinalQualityCheck <<choice>>
    PreparingForPullRequest --> FinalQualityCheck : quality gates re-run after cleanup
    FinalQualityCheck --> CreatingPullRequest : all passed
    FinalQualityCheck --> GeneratingCode : failed, retries remaining
    FinalQualityCheck --> CreatingPullRequest : failed, retries exhausted (draft PR)

    CreatingPullRequest --> ReflectingOnRun
    ReflectingOnRun --> SyncingBrainRepoPostRun
    SyncingBrainRepoPostRun --> Completed

    Completed --> [*]
    Failed --> [*]
    Cancelled --> [*]

    note right of CloningRepository
        Label swapped to agent in-progress
    end note
    note right of ConfidenceGate
        blockingIssues non-empty forces not_ready
    end note
    note right of QualityGateDecision
        Agent gets error feedback and fixes before re-check
    end note
    note left of CreatingPullRequest
        Draft PR sets agent error label. Normal PR adds agent done label.
    end note
    note left of ReflectingOnRun
        Only if brain repo configured and not read-only.
        Feedback collected here (success path).
    end note
```

## Pipeline Steps

```
Created → CloningRepository → RunningEnvironmentSetup → SyncingBrainRepoPreRun → CreatingBranch
  → VerifyingBaseline → AnalyzingCode → ReviewingAnalysis → PostingAnalysis → [Confidence Gate]
  → GeneratingCode → ReviewingCode → RunningQualityGates → [Quality Gate Decision]
  → PreparingForPullRequest → [Final Quality Gate]
  → CreatingPullRequest → ReflectingOnRun → SyncingBrainRepoPostRun → Completed
```

Each step is represented by the `PipelineStep` enum. The pipeline tracks both the current step and a `HighWaterMark` (highest step ever reached), which the UI uses to show revisited steps during retries.

## State Descriptions

| Step | What Happens |
|------|-------------|
| **Created** | Run initialized, providers resolved and validated |
| **CloningRepository** | Repository cloned to a fresh workspace directory. Label swapped to `agent:in-progress` |
| **RunningEnvironmentSetup** | Executes provider-defined setup steps (e.g., package restore, auth configuration) with injected secrets. Non-fatal steps abort the run on non-zero exit |
| **SyncingBrainRepoPreRun** | Brain repository synced into workspace (if configured). Non-fatal on failure |
| **CreatingBranch** | Feature branch created from default branch (format: `feature/auto-{issueNumber}-{slug}-{runId}`) |
| **VerifyingBaseline** | Baseline health check — runs build/tests on the default branch before the agent writes code. Catches broken base branches early. Skipped when `BaselineHealthCheckEnabled` is false |
| **AnalyzingCode** | Agent analyzes the issue and codebase, writes `analysis.md` and `analysis-assessment.json` |
| **ReviewingAnalysis** | Adversarial review of the analysis — validates completeness, flags gaps (when `AnalysisReviewEnabled` is true) |
| **PostingAnalysis** | Analysis comment posted to the GitHub issue |
| **GeneratingCode** | Agent implements the changes. Also used during quality gate retries |
| **ReviewingCode** | Multi-agent code review: each review agent writes findings, then a fix agent addresses `[CRITICAL]` items |
| **RunningQualityGates** | Build, tests, coverage, and external CI checks run |
| **PreparingForPullRequest** | Agent cleans up the working directory (removes debug artifacts, unused code, formatting). Quality gates run one final time after cleanup |
| **CreatingPullRequest** | PR created (normal or draft). Blacklisted file detection happens here |
| **ReflectingOnRun** | Agent reviews the entire run and enriches `.brain/` knowledge (if brain repo configured). Feedback collected here — questions appended to the reflection prompt |
| **SyncingBrainRepoPostRun** | Brain updates committed and pushed to brain repository |
| **Completed** | Terminal state — run succeeded (or `wont_do` assessment) |
| **Failed** | Terminal state — unrecoverable error or retries exhausted |
| **Cancelled** | Terminal state — user cancelled the run |

## Confidence Gate

After the analysis phase, the pipeline evaluates the agent's structured assessment (`analysis-assessment.json`):

```mermaid
flowchart TD
    PA[PostingAnalysis] --> CG{Confidence Gate}
    CG -->|ready| GC[GeneratingCode]
    CG -->|not_ready| F[Failed\nagent needs-refinement]
    CG -->|wont_do| C[Completed\nagent wont-do]
```

- **`ready`** — proceed to code generation
- **`not_ready`** — abort, label `agent:needs-refinement`, post blocking issues to GitHub
- **`wont_do`** — mark Completed, label `agent:wont-do`, post reasoning to GitHub

Override rule: if `blockingIssues` is non-empty, the gate forces `not_ready` regardless of the recommendation value. Unknown recommendation values (e.g. typos) fall through as `ready` (fail-open design).

## Quality Gate Retry Loop

After code generation and review, quality gates run. If they fail, the pipeline enters a retry loop:

```mermaid
flowchart TD
    RQG[RunningQualityGates] --> GP{Gates Passed?}
    GP -->|yes| PREP[PreparingForPullRequest\nagent cleanup]
    GP -->|no| RL{retries remaining?}
    RL -->|yes| GC[GeneratingCode\nagent gets error feedback]
    RL -->|no| FF[CollectFailureFeedback\n60s agent call]
    FF --> DPR[Draft PR\nagent error label]
    GC --> RQG2[RunningQualityGates\nre-validate]
    PREP --> FQG[RunningFinalQualityGates]
    FQG -->|pass| PR[CreatingPullRequest]
    FQG -->|fail| RL2{retries remaining?}
    RL2 -->|yes| GC
    RL2 -->|no| DPR
```

Quality gates checked (in order):
1. **Compilation** — Build command must succeed with 0 errors
2. **Tests** — Test command must have 0 failures
3. **Coverage** — Code coverage must meet `coverageThreshold` (if configured). Supports Cobertura XML (Python, .NET) and JaCoCo XML (Java) formats
4. **External CI** — External CI pipeline must pass (if enabled). Requires commit + push before checking

External CI is only evaluated after local gates (compilation, tests, coverage) pass. If external CI fails, it does not enter the agent retry loop — the failure goes straight to a draft PR. Only local gate failures trigger retries with agent error feedback.

The retry prompt includes the full gate failure details and points the agent to diagnostic output files. Each retry attempt is a `--resume` call, so the agent has full conversation history.

If all retries are exhausted, a **draft PR** is created with the failing code, and the issue is labeled `agent:error`.

## Label Transitions

```mermaid
stateDiagram-v2
    direction LR
    state "no label" as none
    state "agent next" as next
    state "agent in-progress" as ip
    state "agent done" as done
    state "agent needs-refinement" as nr
    state "agent wont-do" as wd
    state "agent error" as err
    state "agent cancelled" as cancel

    none --> next : user adds label
    next --> ip : pipeline starts
    ip --> done : success
    ip --> nr : not_ready
    ip --> wd : wont_do
    ip --> err : error / timeout
    ip --> cancel : user cancels
    done --> next : user requests rework
    err --> next : user re-queues
    nr --> next : user refines issue
    wd --> next : user disagrees
    cancel --> next : user re-queues
```

Re-queueing from `agent:error` or `agent:needs-refinement` requires manual dispatch via the web UI — closed-loop mode skips issues that still carry these labels. Re-queueing from `agent:wont-do` or `agent:cancelled` works in both manual and closed-loop modes.

## Error Handling

Any step can transition to `Failed` on error. The pipeline catches exceptions at each phase boundary and records the failure reason. Specific behaviors:

- **Clone failure** — immediate fail, no retry
- **Analysis failure** — retries up to `maxAnalysisRetries` (assessment file missing, malformed JSON, analysis too short)
- **Agent timeout** — fail with exit code 124
- **Blacklisted files** — excluded from commits with a warning logged
- **External CI timeout** — treated as gate failure, enters retry loop
- **Cancellation** — `OperationCanceledException` caught at top level, label set to `agent:cancelled`

---

## PR Review Pipeline

The PR review pipeline is a parallel workflow that processes pull requests for automated code review. It reuses the same dispatch mechanism (`agent:next` label polling), the same step execution pattern, and the same agent execution infrastructure — but with a shorter step sequence that skips analysis, code generation, and quality gates.

### Overview

```mermaid
flowchart TD
    A[PipelineLoopService] -->|Poll cycle| B{Template ReviewEnabled?}
    B -->|Yes| C[ListOpenPullRequestsAsync<br/>label: agent:next]
    B -->|No| D[Skip PR polling]
    C --> E{PRs found?}
    E -->|Yes| F[Filter: skip in-progress]
    F --> G[TryDispatchReviewAsync]
    G --> H[Agent picks up job]
    H --> I[PR Review Step Sequence]
    
    subgraph "PR Review Step Sequence"
        I --> S1[1. CloneRepositoryStep]
        S1 --> S1b[2. EnsureAgentGitignoreStep]
        S1b --> S1c[3. WriteMcpConfigStep]
        S1c --> S1d[4. WriteSteeringStep]
        S1d --> S2[5. CreateBranchStep<br/>checkout PR branch, no merge]
        S2 --> S3[6. SyncBrainPreRunStep<br/>optional]
        S3 --> S4[7. ExtractLinkedIssuesStep<br/>includes PR conversation context]
        S4 --> S5[8. ReviewCodeStep]
        S5 --> S6[9. PostReviewFindingsStep]
    end
```

### Review Step Sequence

The PR review pipeline executes 9 steps (compared to 15+ for implementation):

| # | Step | Reuses Existing | Description |
|---|------|:---:|-------------|
| 1 | `CloneRepositoryStep` | ✅ | Clone the repository to a fresh workspace |
| 2 | `EnsureAgentGitignoreStep` | ✅ | Ensure `.agent/` is in `.gitignore` |
| 3 | `WriteMcpConfigStep` | ✅ | Write MCP server configuration for the agent |
| 4 | `WriteSteeringStep` | ✅ | Write pipeline steering content to the workspace |
| 5 | `CreateBranchStep` | ✅ | Check out the PR branch (rework path, skip merge from base) |
| 6 | `SyncBrainPreRunStep` | ✅ | Sync brain repository if configured (non-fatal on failure) |
| 7 | `ExtractLinkedIssuesStep` | ❌ | Extract linked issues, write context files, write PR conversation context |
| 8 | `ReviewCodeStep` | ✅ | Resolve reviewer configs and execute multi-agent code review |
| 9 | `PostReviewFindingsStep` | ❌ | Format findings and post as PR review comment |

Key differences from the implementation pipeline:
- **No analysis step** — the PR already contains the implementation
- **No code generation** — the review is read-only
- **No quality gates** — build/test feedback comes from existing CI
- **No merge from base** — the PR branch is reviewed as-is
- **No post-run brain sync** — reviews don't produce new knowledge to persist
- **Adds infrastructure steps** — EnsureAgentGitignore, WriteMcpConfig, and WriteSteering run before branch checkout
- **PR conversation context** — written within `ExtractLinkedIssuesStep` (not a separate step)

### Review Run State Machine

```mermaid
stateDiagram-v2
    [*] --> Created
    Created --> CloningRepository
    CloningRepository --> CreatingBranch
    CreatingBranch --> SyncingBrainRepoPreRun : if brain configured
    CreatingBranch --> ExtractingLinkedIssues : no brain
    SyncingBrainRepoPreRun --> ExtractingLinkedIssues
    ExtractingLinkedIssues --> ReviewingCode
    ReviewingCode --> PostingFindings
    PostingFindings --> Completed
    
    CloningRepository --> Failed
    CreatingBranch --> Failed
    ReviewingCode --> Failed
    Created --> Cancelled
```

> **Note:** Infrastructure steps (EnsureAgentGitignore, WriteMcpConfig, WriteSteering) execute between Clone and CreateBranch but do not have dedicated `PipelineStep` enum values — they run transparently within the `CloningRepository` phase.

### PR Label Lifecycle

PR review runs follow the same label lifecycle as implementation runs:

```mermaid
stateDiagram-v2
    direction LR
    state "agent next" as next
    state "agent in-progress" as ip
    state "agent done" as done
    state "agent error" as err
    state "agent cancelled" as cancel

    next --> ip : review starts
    ip --> done : review succeeds
    ip --> err : review fails
    ip --> cancel : user cancels
    done --> next : user requests re-review
    err --> next : user re-queues
```

- **Dispatch**: `agent:next` → `agent:in-progress`
- **Success**: `agent:in-progress` → `agent:done`
- **Failure**: `agent:in-progress` → `agent:error`
- **Cancellation**: `agent:in-progress` → `agent:cancelled`

Re-review is always explicitly triggered by the user (remove `agent:done`, re-add `agent:next`). New commits alone do NOT trigger re-review.

### Loop Mode Configuration

Each `PipelineJobTemplate` has three independent toggles controlling which work types it processes:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ImplementationEnabled` | `bool` | `true` | Template polls for issues and dispatches implementation jobs |
| `ReviewEnabled` | `bool` | `true` | Template polls for PRs and dispatches review jobs |
| `DecompositionEnabled` | `bool` | `false` | Template polls for epics and dispatches decomposition jobs |

The existing `Enabled` property acts as a master switch — when `false`, all work types (implementation, review, and decomposition) are disabled regardless of individual flags.

#### Configuration Examples

**Both enabled (default):**
```json
{
  "Name": "Full Pipeline",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": true
}
```

**Review-only template** (dedicated to PR reviews, no implementation):
```json
{
  "Name": "Review Only",
  "Enabled": true,
  "ImplementationEnabled": false,
  "ReviewEnabled": true
}
```

**Implementation-only template** (no PR reviews):
```json
{
  "Name": "Implementation Only",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": false
}
```

Settings are read at the start of each poll cycle, allowing runtime changes via the configuration UI without restarting the loop.

### Dispatch Budget Sharing

When multiple work type loops are active, they share the `ClosedLoopMaxRunsPerCycle` budget. The pipeline alternates fairly between issue, PR, and decomposition queues (round-robin) to prevent starvation of any work type.

- Total dispatches per cycle never exceed `ClosedLoopMaxRunsPerCycle`
- All active queues get at least one dispatch when budget allows
- PRs are processed in FIFO order (oldest `CreatedAt` first)
- Draft PRs are included in review dispatch (a warning is shown in the UI)
- PRs with `agent:error`, `agent:in-progress`, `agent:done`, or `agent:cancelled` labels are skipped
- Decomposition dispatch is additionally gated by `MaxConcurrentDecompositions`

### Issue Dependency Tracking

The dispatch loop supports dependency tracking for issues. When an issue body contains dependency references, the dispatcher checks whether those referenced issues are closed before dispatching. This prevents issues from being worked on before their prerequisites are complete.

#### Supported Dependency Patterns

The following patterns are recognized in the issue body (case-insensitive, word-boundary matched):

| Pattern | Example |
|---------|---------|
| `Blocked by #N` | `Blocked by #123` |
| `Depends on #N` | `Depends on #45` |
| `Requires #N` | `Requires #78` |
| `After #N` | `After #90` |

Multiple dependencies can appear in a single issue body — ALL must be satisfied (closed) before the issue is dispatched. Patterns are matched with a `\b` word boundary before the keyword, so "hereafter #123" does NOT match but "After #123" does.

#### How It Works

The dependency check is **stateless** and **body-parsed** on each poll cycle:

1. When a candidate issue is dequeued for dispatch, the `DependencyParser` extracts issue numbers from the body text
2. For each referenced issue number, the `DependencyChecker` calls `IsIssueClosedAsync` on the issue provider
3. Results are **cached per-cycle** in a shared `Dictionary<int, bool>` — if multiple candidates reference the same dependency, only one API call is made
4. If ALL dependencies are closed → issue is eligible for dispatch
5. If ANY dependency is still open → issue is skipped, next candidate in the queue is tried

No internal state is persisted between cycles. No new labels are introduced. The check runs fresh on every poll cycle (~30s default interval).

#### Behavior When Dependencies Are Unresolved

- The issue stays labeled `agent:next` (no label change)
- It is skipped silently from dispatch this cycle
- On the next poll cycle (~30s), the check runs again
- Once all dependencies are closed, the issue becomes eligible for dispatch
- A blocked issue at the front of the queue does NOT prevent unblocked issues behind it from being dispatched

#### Graceful Degradation

- **API failures** (network errors, rate limits, 5xx after retry exhaustion) → the dependency is treated as unresolved → the issue is skipped for this cycle, a warning is logged
- **Issue not found** (404) → treated as unresolved (conservative: don't dispatch if we can't verify)
- **Null body** → treated as no dependencies (issue dispatches normally)
- Failures for one issue do NOT affect other issues in the same cycle
- Failures do NOT cause the poll cycle to abort, the circuit breaker to trip, or the template status to record a failure

#### Known Limitations

| Limitation | Description |
|------------|-------------|
| "After" false positives | The word "after" is common in English. Sentences like "I fixed this after #456 was reported" will incorrectly match as a dependency. Prefer `Blocked by` or `Depends on` for human-written issues. |
| Code block matching | Patterns inside markdown code blocks (`` `Blocked by #123` `` or fenced blocks) will still match. Avoid putting dependency patterns in code examples within issue bodies. |
| Strikethrough matching | `~~Blocked by #123~~` will still match. Delete dependency text rather than striking through. |
| No circular dependency detection | If issue A depends on B and B depends on A, both are skipped indefinitely. Humans must notice and fix. |
| Same-repository only | Only `#N` references are supported. Cross-repository references (`owner/repo#N`) are not recognized. |

### Linked Issue Extraction

The review pipeline extracts linked issues from the PR to provide requirements context to the review agent. This enables the reviewer to evaluate the PR against the original acceptance criteria.

#### Extraction Priority Order

Each repository provider implements its own extraction logic:

1. **Platform API** — Query the platform's linked/closing references API (e.g., GitHub timeline events)
2. **PR title parsing** — Scan the PR title for issue references
3. **PR body parsing** — Scan the PR body/description for issue references

#### Recognized Patterns (GitHub)

- `#N` — issue number reference
- `owner/repo#N` — cross-repository reference
- `GH-N` — GitHub shorthand
- Closing keywords: `closes #N`, `fixes #N`, `resolves #N` (case-insensitive)

#### How Context is Provided

When linked issues are found:
1. Issue details (title, body) are fetched at dispatch time (orchestrator-side)
2. Pre-fetched issue context is included in the job assignment message
3. The agent writes each linked issue as `.agent/linked-issue-{id}.md` in the workspace
4. The review agent reads these files alongside the PR diff for requirements-aware review

When no linked issue is found, the review proceeds normally using PR metadata (title, description) as context. This is non-blocking — reviews work with or without linked issue context.

#### Multiple Issues

When multiple issue references are found, ALL are retrieved and written as separate files. The review agent infers which issue(s) are most relevant based on the PR title, description, and diff.

### Review Findings Format

Review findings are posted as a PR review comment with the following structure:

```markdown
<!-- agent:pr-review -->
## 🤖 Automated Code Review

**Review Agents**: Correctness, Security, AcceptanceCriteria

| Severity | Count |
|----------|-------|
| [CRITICAL] | 2 |
| [WARNING] | 5 |
| [SUGGESTION] | 3 |

<details>
<summary>Correctness</summary>

[Agent findings here]

</details>

<details>
<summary>Security</summary>

[Agent findings here]

</details>
```

The `<!-- agent:pr-review -->` marker enables the pipeline to detect and update existing reviews on subsequent runs, avoiding duplicate comments.

When no issues are found, the review body states: "✅ No issues found."

When no reviewer configuration matches the repository labels, a comment is posted indicating no applicable reviewers were found, and the run completes with `agent:done`.

### Error Handling (Review Runs)

Review runs follow the same error handling principles as implementation runs:

- **Clone failure** — immediate fail, label set to `agent:error`
- **Checkout failure** — immediate fail, label set to `agent:error`
- **Brain sync failure** — non-fatal, review continues without brain context
- **Review agent timeout** — fail with the configured `AgentTimeout`
- **Posting failure** — non-fatal (review ran successfully, posting failed), logged as warning
- **Cancellation** — label set to `agent:cancelled`


---

## Inline Review Comments

The PR review pipeline supports posting code review findings as native inline comments on specific file:line positions in the diff. This is an additive enhancement — the body-level review summary is always posted regardless of inline comment settings.

### GitHub API Migration

The review submission was migrated from the **Issue Comments API** (`POST /repos/{owner}/{repo}/issues/{issue_number}/comments`) to the **Pull Request Reviews API** (`POST /repos/{owner}/{repo}/pulls/{pull_number}/reviews`). This change means:

- Review comments are now **dismissible reviews** rather than plain issue comments
- Reviews support **inline comments** attached to specific file:line positions in the diff
- Reviews have a **type** (`COMMENT` or `REQUEST_CHANGES`) that affects PR merge status
- Previous automated reviews can be **dismissed** via the API before posting new ones

Existing issue-comment-based reviews on open PRs (from before the migration) are not affected — they remain as-is with their `<!-- agent:pr-review-superseded -->` collapse markers.

### Structured Output Format

When inline comments are enabled, the review prompt instructs agents to format findings with file:line references:

```
[SEVERITY] path/to/file.ext:LINE — description of the issue
```

Where:
- `SEVERITY` is one of: `CRITICAL`, `WARNING`, `SUGGESTION`
- `path` is relative to the repository root using forward slashes
- `LINE` is a 1-based line number in the file
- `description` explains the finding

Examples:
```
[CRITICAL] src/Service.cs:42 — Null reference possible when input is not validated
[WARNING] src/Controllers/UserController.cs:15 — Missing input validation on email parameter
[SUGGESTION] src/Utils/StringHelper.cs:8 — Consider using StringBuilder for repeated concatenation
```

For findings without a specific file location:
```
[WARNING] — General observation about architecture
```

The `FindingsParser` recognizes four file:line reference formats:
- `path/to/file.cs:42`
- `path/to/file.cs#L42`
- `path/to/file.cs (line 42)`
- `path/to/file.cs, line 42`

### Inline Comment Flow

The full flow within `PostReviewFindingsStep`:

```
Parse → Filter → Cap → Consolidate → Submit
```

Detailed sequence:

```mermaid
flowchart TD
    A[PostReviewFindingsStep] --> B{SupportsInlineReviewComments?}
    B -->|Yes| C[DismissPreviousReviewAsync]
    B -->|No| D[CollapseExistingReviews<br/>existing behavior]
    C --> E[Format body via ReviewFindingsFormatter]
    D --> E
    E --> F{InlineComments.Enabled?}
    F -->|No| G[Submit body-only review]
    F -->|Yes| H[FindingsParser.Parse per agent]
    H --> I{Agent has markers but<br/>no file:line findings?}
    I -->|Yes| J[ExecuteFollowUpAsync<br/>retry up to MaxRetries]
    I -->|No| K[FindingsSelector.Select]
    J --> K
    K --> L[Build ReviewSubmission<br/>with CommitId]
    L --> M[SubmitPullRequestReviewAsync]
    M -->|Success| N[Track InlineCommentsPosted]
    M -->|422 or Exception| O[Retry body-only]
    O -->|Success| P[Track degradation]
    O -->|Failure| Q[Log warning, return Continue]
```

1. **Dismiss/Collapse** — If the provider supports inline reviews, dismiss previous bot reviews via the Reviews API. Otherwise, collapse existing review comments (existing behavior).
2. **Format body** — Generate the summary body via `ReviewFindingsFormatter.Format` (unchanged).
3. **Check enabled** — If `InlineComments.Enabled` is `false`, submit body-only and return.
4. **Parse** — Run `FindingsParser.Parse` on each agent's output to extract `StructuredFinding` entries.
5. **Retry** — For agents that produced severity markers but no file:line references, invoke `ExecuteFollowUpAsync` (a fresh prompt with the original output + reformat instructions) up to `MaxRetries` times.
6. **Select** — `FindingsSelector` applies the transformation pipeline: filter by `SeverityThreshold` → stable sort by severity (if `OrderBySeverity`) → cap at `MaxInlineComments` → consolidate same file:line into single comments.
7. **Submit** — Build a `ReviewSubmission` with the body, inline comments, and HEAD commit SHA. Submit via the Reviews API.
8. **Degrade** — On HTTP 422 or any exception, retry once without inline comments (body-only). If that also fails, log a warning and return `StepResult.Continue`. The pipeline never fails due to inline comment issues.

### Retry and Degradation Behavior

#### Per-Agent Retry

Retries are **per-agent**, not global. If Agent A produces structured findings but Agent B doesn't, only B is retried.

- Each retry invokes `ExecuteFollowUpAsync` — a fresh LLM call with the agent's original output + reformat instructions
- The retry counter is per-agent, capped at `MaxRetries` (default: 1)
- Retries are sequential (consistent with existing review execution)
- If the retry produces structured output, it replaces the original output for that agent

#### Degradation Scenarios

| Scenario | Behavior |
|----------|----------|
| Agent doesn't produce structured output after retries | Findings appear in body summary only (no inline comments for that agent) |
| GitHub returns HTTP 422 (Validation Failed) | Retry once without ALL inline comments (body-only). GitHub's 422 doesn't identify which comment failed |
| Body-only retry also fails | Exception caught, logged as warning, step returns `Continue` |
| `SupportsInlineReviewComments` is `false` | Inline comments rendered in body under "📍 Findings by Location" section |
| `InlineComments.Enabled` is `false` | No parsing, no prompt enhancement, body-only review (pre-feature behavior) |

#### Observability

The `PipelineRun` tracks inline comment outcomes:

| Property | Type | Description |
|----------|------|-------------|
| `InlineCommentsPosted` | `int` | Number of inline comments successfully submitted |
| `InlineCommentsDegraded` | `bool` | Whether fallback to body-only occurred |
| `InlineCommentsDegradedReason` | `string?` | Reason for degradation (null on success) |

### `SupportsInlineReviewComments` Capability Detection

The `IRepositoryProvider` interface exposes a `SupportsInlineReviewComments` property:

```csharp
bool SupportsInlineReviewComments => false; // default: conservative
```

- **GitHub provider** returns `true` — supports inline comments and review dismissal
- **GitLab provider** returns `true` — supports inline comments via merge request discussions
- **Default** returns `false` — providers that haven't opted in get body-only behavior

When `SupportsInlineReviewComments` is `false` but the `FindingsParser` extracted findings with location metadata, the pipeline appends a **"📍 Findings by Location"** section to the body comment. Findings are grouped by file path (alphabetical), with each finding as a bullet point showing severity emoji, line number, and message:

```markdown
### 📍 Findings by Location

#### src/Controllers/UserController.cs
- 🔴 Line 42: Null reference possible when input is not validated
- 🟡 Line 15: Missing input validation on email parameter

#### src/Utils/StringHelper.cs
- 💡 Line 8: Consider using StringBuilder for repeated concatenation
```

### Stale Review Handling

Before posting a new review, the pipeline handles previous automated reviews:

- **GitHub (inline-capable)**: Calls `DismissPreviousReviewAsync` to dismiss all previous bot reviews containing the `<!-- agent:pr-review -->` marker. Uses the authenticated bot identity to filter reviews.
- **Non-inline providers**: Falls back to the existing collapse behavior (wrapping previous review bodies in `<details>` blocks with the `<!-- agent:pr-review-superseded -->` marker).

The coupling between `SupportsInlineReviewComments` and dismiss support is intentional — if a provider supports inline comments, it also supports review dismissal.

### `InlineCommentSettings` Configuration

The `InlineCommentSettings` record is nested within `CodeReviewConfiguration`:

```json
{
  "CodeReview": {
    "MaxIterations": 2,
    "FixPrompt": null,
    "ReviewIsolation": "Isolated",
    "InlineComments": {
      "Enabled": true,
      "SeverityThreshold": "Warning",
      "MaxInlineComments": 15,
      "OrderBySeverity": true,
      "MaxRetries": 1
    }
  }
}
```

#### Property Reference

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `Enabled` | `bool` | `true` | — | Master switch. When false, body-only reviews are posted (existing behavior). Defaults to enabled |
| `SeverityThreshold` | `FindingSeverity` | `Warning` | `Suggestion`, `Warning`, `Critical` | Minimum severity for inline posting. A finding is eligible when its severity value `>=` the threshold value |
| `MaxInlineComments` | `int` | `15` | 1–50 | Maximum inline comments per review. Highest-severity findings are prioritized when the cap is reached |
| `OrderBySeverity` | `bool` | `true` | — | Sort eligible findings by severity (Critical first) when selecting which to post inline |
| `MaxRetries` | `int` | `1` | 0–5 | Retry attempts per agent when structured output is missing. Each retry = 1 additional LLM API call |

#### Backward Compatibility

- Existing configuration files without the `InlineComments` key deserialize to a default `InlineCommentSettings` instance with `Enabled = true`
- No migration required — existing deployments are unaffected on upgrade
- Property ranges are validated at usage time via `Math.Clamp` (not at deserialization), consistent with other pipeline configs

#### Cost Awareness

Each retry invokes an additional LLM API call per agent. With `MaxRetries = 1` (default) and 3 review agents, worst case is 3 extra LLM calls per review. Operators should consider this cost tradeoff when increasing `MaxRetries`.


---

## Epic Decomposition Pipeline

The epic decomposition pipeline is a two-phase workflow that transforms high-level epics (GitHub issues labeled `agent:epic`) into implementation-ready sub-issues. It reuses the same dispatch mechanism as implementation and PR review pipelines — `PipelineLoopService` → `IJobDispatcher` → `JobAssignmentMessage` → `LocalPipelineExecutor` — adding two `PipelineRunType` values (`DecompositionAnalysis` and `Decomposition`) that route to dedicated step pipelines.

### Project Context in Decomposition

When a project has an `EpicIssueProviderId` configured, epics from that provider are decomposed with **cross-repository routing**. The decomposition agent receives a project context file (`.agent/project-context.md`) listing all templates in the project with their names, descriptions, and decomposition eligibility. Sub-issues can specify a `targetRepository` to route creation to a different template's issue provider.

See [Projects — Cross-Repo Decomposition](projects.md#cross-repository-decomposition) for the full workflow and configuration details.

### Overview

```mermaid
flowchart TD
    A[PipelineLoopService] -->|Poll cycle| B{Template DecompositionEnabled?}
    B -->|Yes| C[ListOpenIssuesAsync<br/>labels: agent:epic, agent:epic-approved]
    B -->|No| D[Skip decomposition polling]
    C --> E{Epics found?}
    E -->|Yes| F[Filter: skip in-progress, error, done]
    F --> G{Label type?}
    G -->|agent:epic| H[TryDispatchDecompositionAsync<br/>Phase: DecompositionAnalysis]
    G -->|agent:epic-approved| I[TryDispatchDecompositionAsync<br/>Phase: Decomposition]
    H --> J[Agent picks up job]
    I --> J
    J --> K{RunType routing}
    K -->|DecompositionAnalysis| L[Phase 1 Step Pipeline]
    K -->|Decomposition| M[Phase 2 Step Pipeline]

    subgraph "Phase 1: DecompositionAnalysis"
        L --> P1S1[1. CloneRepositoryStep]
        P1S1 --> P1S2[2. SyncBrainPreRunStep]
        P1S2 --> P1S2b[3. WriteProjectContextStep<br/>if project context present]
        P1S2b --> P1S3[4. WriteOpenIssueContextStep]
        P1S3 --> P1S4[5. DecompositionAnalysisStep]
        P1S4 --> P1S5[6. PostDecompositionPlanStep]
    end

    subgraph "Phase 2: Decomposition"
        M --> P2S1[1. CloneRepositoryStep]
        P2S1 --> P2S2[2. SyncBrainPreRunStep]
        P2S2 --> P2S3[3. DecompositionStep]
        P2S3 --> P2S4[4. CreateIssuesStep<br/>with target routing]
        P2S4 --> P2S5[5. PostDecompositionSummaryStep]
    end
```

### Label State Machine

The epic decomposition pipeline uses a dedicated label lifecycle separate from the implementation pipeline:

```mermaid
stateDiagram-v2
    [*] --> AgentEpic : User labels issue
    AgentEpic --> AgentInProgress : Phase 1 dispatched
    AgentInProgress --> AgentEpicReview : Phase 1 success
    AgentInProgress --> AgentError : Phase 1 failure
    AgentEpicReview --> AgentEpic : User requests re-analysis
    AgentEpicReview --> AgentEpicApproved : User approves plan
    AgentEpicApproved --> AgentInProgress : Phase 2 dispatched
    AgentInProgress --> AgentDone : Phase 2 success
    AgentError --> AgentEpic : User retries Phase 1
    AgentError --> AgentEpicApproved : User retries Phase 2
```

#### Label Definitions

| Label | Hex Color | Purpose |
|-------|-----------|---------|
| `agent:epic` | `#7057ff` (purple) | Triggers Phase 1 (DecompositionAnalysis) |
| `agent:epic-review` | `#fbca04` (yellow) | Plan posted, awaiting human approval |
| `agent:epic-approved` | `#0e8a16` (green) | Triggers Phase 2 (Decomposition) |

These labels are auto-created by `EnsureAgentLabelsAsync` alongside existing pipeline labels.

#### Eligibility Filters

When polling for decomposition candidates:

- **Phase 1 (`agent:epic`)**: Skip issues that also carry `agent:epic-review`, `agent:in-progress`, `agent:error`, or `agent:done`
- **Phase 2 (`agent:epic-approved`)**: Skip issues that also carry `agent:in-progress`, `agent:error`, or `agent:done`
- Issues already being processed or queued are skipped (same `IsIssueBeingProcessedOrQueued` check as implementation dispatch)

### Decomposition Loop Integration

The decomposition loop is integrated into `PipelineLoopService` as a third queue alongside implementation and PR review:

#### Three-Way Fair Alternation

When all three work types are active, the pipeline alternates fairly between them (round-robin) to prevent starvation:

```
Issue queue → PR queue → Decomposition queue → Issue queue → ...
```

- Total dispatches per cycle never exceed `ClosedLoopMaxRunsPerCycle`
- All three queues get at least one dispatch when budget allows (budget ≥ 3)
- Decomposition jobs share the same budget as implementation and review

#### Concurrency Limit

`MaxConcurrentDecompositions` is enforced by querying active `PipelineRun` instances filtered by `RunType == DecompositionAnalysis || Decomposition`. When the active count reaches the limit, decomposition dispatch is skipped for that cycle.

### Phase 1: DecompositionAnalysis Step Sequence

| # | Step | PipelineStep Enum | Description |
|---|------|-------------------|-------------|
| 1 | `CloneRepositoryStep` | `CloningRepository` | Clone the repository to a fresh workspace |
| 2 | `SyncBrainPreRunStep` | `SyncingBrainRepoPreRun` | Sync brain repository if configured (non-fatal on failure) |
| 3 | `WriteOpenIssueContextStep` | `DownloadingOpenIssues` | Download open issues for deduplication context |
| 4 | `DecompositionAnalysisStep` | `ExploringCodebase` → `GeneratingPlan` → `ReviewingPlan` | Agent explores codebase, generates plan, adversarial review validates |
| 5 | `PostDecompositionPlanStep` | `PostingPlan` | Post/update plan comment on epic, swap label to `agent:epic-review` |

#### WriteOpenIssueContextStep

Downloads open issues via `IAgentIssueOperations` (proxied through SignalR to the orchestrator) and writes them as markdown files to `.agent/open-issues/{identifier}.md`. Each file contains YAML front-matter (identifier, title, labels) followed by the issue body. The agent reads these to avoid proposing sub-issues that duplicate existing work.

Cap: controlled by `MaxOpenIssuesForContext` (default 50). Individual fetch failures are logged and skipped (graceful degradation).

#### DecompositionAnalysisStep

1. Writes epic issue body + all comments to `.agent/issue-context.md`
2. Builds analysis prompt with `MaxDecompositionSubIssues` cap
3. Executes agent — expects `.agent/decomposition-plan.md` output
4. Validates plan file exists and has ≥20 characters
5. Executes adversarial review via `AdversarialReviewHelper.ExecuteReviewAsync`
6. On success → `StepResult.Continue`; on failure → `StepResult.Stop`

The adversarial review validates: no overlap with open issues, each sub-issue is right-sized (≤5 files, one verification criterion, one agent run), dependencies are acyclic, all epic acceptance criteria are covered, and no duplicate titles.

#### PostDecompositionPlanStep

1. Reads `.agent/decomposition-plan.md` from workspace
2. Formats comment with `<!-- agent:decomposition-plan -->` marker + approval instructions
3. Checks for existing plan comment via `ListCommentsAsync` + marker search (most recent match)
4. If existing: updates via `UpdateCommentAsync`; if not: posts new via `PostCommentAsync`
5. Swaps label to `agent:epic-review`

On re-run, the existing plan comment is updated (not duplicated), identified by the marker.

### Phase 2: Decomposition Step Sequence

| # | Step | PipelineStep Enum | Description |
|---|------|-------------------|-------------|
| 1 | `CloneRepositoryStep` | `CloningRepository` | Clone the repository to a fresh workspace |
| 2 | `SyncBrainPreRunStep` | `SyncingBrainRepoPreRun` | Sync brain repository if configured (non-fatal on failure) |
| 3 | `DecompositionStep` | `GeneratingSubIssues` | Agent produces sub-issue JSON files |
| 4 | `CreateSubIssuesStep` | `CreatingIssues` | Parse JSON, resolve dependencies, create issues sequentially |
| 5 | `PostDecompositionSummaryStep` | `PostingSummary` | Post summary comment, swap label to `agent:done` |

#### DecompositionStep

1. Writes epic body + all comments (including plan comment) to `.agent/issue-context.md`
2. Queries existing `agent:generated` sub-issues for deduplication context
3. Builds decomposition prompt with `MaxDecompositionSubIssues` cap
4. Executes agent — expects `.agent/sub-issues/*.json` output
5. Validates plan comment exists (marker detection)

#### CreateSubIssuesStep

1. Parses sub-issue files via `SubIssueFileParser` (alphabetical order)
2. Enforces `MaxDecompositionSubIssues` cap (takes first N alphabetically)
3. Creates issues sequentially via `IAgentIssueOperations.CreateIssueAsync`
4. For each issue: sanitizes title (`TextSanitizer.SanitizeTitle`), sanitizes body (`TextSanitizer.SanitizeMarkdown`), resolves dependencies via `DependencyResolver`
5. Applies labels: `agent:next` + `agent:generated` + custom labels from JSON
6. Retries transient errors (3 attempts, exponential backoff: 0s, 1s, 3s)
7. 5-minute creation phase timeout — remaining issues marked as failed on timeout
8. Tracks results in `List<SubIssueCreationResult>` for the summary step

#### PostDecompositionSummaryStep

1. Reads `SubIssueResults` from the pipeline run
2. Formats summary comment with `<!-- agent:decomposition-summary -->` marker + table of created/failed issues
3. Posts via `PostCommentAsync`
4. All succeeded → swap to `agent:done`; all failed → swap to `agent:error`; partial success → swap to `agent:done`
5. Summary post failure → log error, proceed with label swap

### Template Configuration

The `DecompositionEnabled` property on `PipelineJobTemplate` controls whether a template participates in decomposition polling. It operates independently of `ImplementationEnabled` and `ReviewEnabled`.

#### Configuration Examples

**All pipelines enabled:**
```json
{
  "Name": "Full Pipeline",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": true,
  "DecompositionEnabled": true
}
```

**Decomposition-only template** (dedicated to epic breakdown):
```json
{
  "Name": "Decomposition Only",
  "Enabled": true,
  "ImplementationEnabled": false,
  "ReviewEnabled": false,
  "DecompositionEnabled": true
}
```

**Implementation + decomposition** (no PR reviews):
```json
{
  "Name": "Implement and Decompose",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": false,
  "DecompositionEnabled": true
}
```

The `Enabled` property acts as a master switch — when `false`, all polling (implementation, review, and decomposition) is disabled regardless of individual flags.

Settings are read at the start of each poll cycle, allowing runtime changes via the configuration UI without restarting the loop.

#### Pipeline Configuration Properties

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `MaxDecompositionSubIssues` | `int` | `10` | 1–20 | Maximum sub-issues the agent may propose per epic. Controls both the prompt instruction and the executor's creation cap |
| `MaxConcurrentDecompositions` | `int` | `2` | — | Maximum decomposition runs (across both phases) executing simultaneously. Dispatch skipped when limit reached |
| `DecompositionTimeout` | `TimeSpan` | `15 min` | — | Timeout for decomposition phases (separate from `AgentTimeout`). Accounts for codebase exploration time |
| `MaxOpenIssuesForContext` | `int` | `50` | 1+ | Maximum open issues downloaded for deduplication context |

### Sub-Issue JSON Schema

The agent writes sub-issue files to `.agent/sub-issues/` as JSON, one file per sub-issue.

#### File Naming Convention

```
{NN}-{title-slug}.json
```

- `{NN}` — Zero-padded two-digit sequence number starting at `01`
- `{title-slug}` — Lowercase, hyphen-separated slug derived from the title (max 60 characters, truncated without breaking words)

Examples: `01-add-user-authentication.json`, `02-create-database-schema.json`

#### Schema

```json
{
  "title": "Add user authentication endpoint",
  "body": "## Summary\n\nImplement the /auth/login endpoint...\n\n## Affected Components\n\n- `src/Controllers/AuthController.cs`\n\n## Requirements\n\n- Must validate JWT tokens\n\n## Acceptance Criteria\n\n- [ ] POST /auth/login returns 200 with valid credentials",
  "dependencies": ["Create database schema"],
  "labels": ["enhancement"]
}
```

#### Field Reference

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `title` | `string` | Yes | Non-empty, max 256 characters |
| `body` | `string` | Yes | Non-empty, markdown-formatted. Must contain at minimum: Summary, Affected Components, Requirements, Acceptance Criteria sections |
| `dependencies` | `string[]` | Yes | Zero or more title strings referencing other sub-issues in this decomposition (resolved to `#N` format during creation) |
| `labels` | `string[]` | Yes | Zero or more additional labels beyond the auto-applied `agent:next` and `agent:generated` |

#### Validation Rules

- Files must be valid UTF-8 JSON without byte-order mark
- All four fields are required and must have correct types
- `title` and `body` must be non-empty strings
- `dependencies` and `labels` must be arrays (may be empty)
- Invalid files are logged and skipped without failing the phase

#### Dependency Resolution

Dependencies are expressed as title-based references (not issue numbers, since numbers aren't known until creation time). During sequential creation, the executor:

1. Creates issues in alphabetical file-name order
2. Maintains a title→issue-number mapping as each issue is created
3. Resolves dependency titles to `#N` format using case-insensitive, whitespace-trimmed matching
4. Inserts "Depends on #N" lines at the top of the sub-issue body before creation
5. Unresolved titles (including forward references) are logged as warnings and omitted

### Workspace Conventions

Decomposition workspaces follow the same conventions as implementation and review workspaces:

#### Workspace Path

```
{WorkspaceBaseDirectory}/decomposition/{runId}/
```

Where `runId` is a GUID identifying the decomposition run.

#### Agent Workspace Paths

| Path | Purpose |
|------|---------|
| `.agent/open-issues/` | Open issue context files for deduplication |
| `.agent/sub-issues/` | Sub-issue JSON output files |
| `.agent/decomposition-plan.md` | Decomposition plan output (Phase 1) |
| `.agent/decomposition-review.md` | Adversarial review findings (Phase 1) |
| `.agent/issue-context.md` | Epic body + comments context |

All paths are defined as constants in `AgentWorkspacePaths`.

#### Workspace Cleanup

- **Success**: Workspace deleted recursively after run completes
- **Failure**: Workspace retained for `FailedWorkspaceRetentionDays` (configurable)
- **Deletion failure**: Logged as warning, execution continues

### Comment Markers

| Marker | Purpose |
|--------|---------|
| `<!-- agent:decomposition-plan -->` | Identifies the decomposition plan comment (first line of comment body) |
| `<!-- agent:decomposition-summary -->` | Identifies the decomposition summary comment |

Markers enable idempotent updates on re-run (update existing comment rather than posting duplicates). Plan comments are identified as the most recent comment containing the marker (marker-only detection, consistent with existing analysis comment identification).

### Partial Failure Handling

The decomposition pipeline handles failures gracefully:

| Scenario | Behavior |
|----------|----------|
| Phase 1 agent error/timeout | Label → `agent:error`, failure logged |
| Phase 1 adversarial review failure | Label → `agent:error`, failure logged |
| Phase 1 plan comment post failure | Label → `agent:error` |
| Phase 2 individual sub-issue creation failure (transient) | Retry 3× with exponential backoff (0s, 1s, 3s) |
| Phase 2 individual sub-issue creation failure (non-transient) | Skip, continue creating remaining issues |
| Phase 2 creation timeout (5 min) | Mark remaining as failed, proceed to summary |
| Phase 2 all creations failed | Label → `agent:error`, summary lists failures |
| Phase 2 partial success | Label → `agent:done`, summary lists successes and failures |
| Summary comment post failure | Log error, proceed with label swap |

Already-created sub-issues are never rolled back — they remain with their `agent:next` labels even if the overall phase fails.

### Re-run Support

To re-run Phase 1 after providing feedback:

1. Post a comment on the epic with your feedback (rejection reasons, requested changes)
2. Remove `agent:epic-review` and add `agent:epic`
3. The pipeline picks up the epic on the next poll cycle
4. The agent receives the full comment thread (including previous plan + your feedback) as context
5. The existing plan comment is updated (not duplicated) with the revised plan

The decomposition prompt instructs the agent to look for comments posted after the previous plan comment as rejection feedback and address them in the revised plan.

### Error Recovery

| Error State | Recovery Action |
|-------------|----------------|
| Phase 1 failed (`agent:error`) | Remove `agent:error`, add `agent:epic` → re-runs Phase 1 |
| Phase 2 failed (`agent:error`) | Remove `agent:error`, add `agent:epic-approved` → re-runs Phase 2 (skips re-analysis) |
| Phase 2 failed (`agent:error`) | Remove `agent:error`, add `agent:epic` → re-runs from Phase 1 |

### Issue Operations Proxy

Consistent with the implementation and review pipelines, the decomposition agent does NOT receive `IIssueProvider` credentials directly. All issue operations are proxied through SignalR to the orchestrator:

| Operation | Agent Method | Hub Method |
|-----------|-------------|------------|
| Create issue | `CreateIssueAsync` | `RequestCreateIssue` |
| List open issues | `ListOpenIssuesAsync` | `RequestListOpenIssues` |
| Get issue details | `GetIssueAsync` | `RequestGetIssue` |
| List comments | `ListCommentsAsync` | `RequestListComments` |
| Update comment | `UpdateCommentAsync` | `RequestUpdateComment` |

The orchestrator resolves the `IIssueProvider` from the run's `IssueProviderConfigId` and executes the operation. This keeps the agent's credential surface minimal.
