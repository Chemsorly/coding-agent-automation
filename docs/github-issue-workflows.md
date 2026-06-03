# User Interaction via GitHub Issues

The pipeline is driven entirely through GitHub issue labels. Users never interact with the pipeline directly — they manage issues on GitHub, and the pipeline reacts to label changes.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for the internal state machine, and [Configuration](configuration.md) for closed-loop settings.

## Labels

The pipeline uses these `agent:*` labels (created automatically on first run):

| Label | Color | Meaning |
|-------|-------|---------|
| `agent:next` | 🟢 Green | Issue is queued for the pipeline to pick up |
| `agent:in-progress` | 🔵 Blue | Pipeline is actively working on this issue |
| `agent:error` | 🔴 Red | Pipeline failed (build errors, timeout, etc.) |
| `agent:needs-refinement` | 🟡 Yellow | Confidence gate rejected — issue needs more detail |
| `agent:wont-do` | ⚪ Gray | Agent determined no code changes are needed |
| `agent:done` | 🔵 Blue | Pipeline completed, PR awaiting review |
| `agent:cancelled` | 🟣 Light blue | Pipeline run was cancelled by user |
| `agent:epic` | 🟣 Purple | Epic queued for decomposition analysis |
| `agent:epic-review` | 🟡 Yellow | Decomposition plan posted, awaiting human approval |
| `agent:epic-approved` | 🟢 Green | Plan approved, queued for sub-issue creation |

Only one `agent:*` label should be present on an issue at a time. The pipeline swaps labels atomically (removes all, then adds the new one).

## Flow 1: Happy Path

1. **User** adds `agent:next` label to a GitHub issue
2. **Pipeline** picks it up (manually via the web UI, or automatically in closed-loop mode)
3. **Pipeline** swaps label to `agent:in-progress`
4. **Pipeline** analyzes the issue, generates code, runs quality gates, creates a PR
5. **Pipeline** adds `agent:done` label on success
6. **User** reviews and merges the PR

## Flow 2: Confidence Gate Rejection (Needs Refinement)

1. **User** adds `agent:next` label
2. **Pipeline** runs the analysis agent, which determines the issue is too vague or has blockers
3. **Pipeline** posts two comments to the issue:
   - `## 🤖 Agent Analysis` — the agent's analysis of the codebase
   - `## ⚠️ Analysis Gate: Needs Refinement` — blocking issues and concerns
4. **Pipeline** swaps label to `agent:needs-refinement`
5. **User** reads the feedback, edits the issue description to address blocking issues
6. **User** removes `agent:needs-refinement` and re-adds `agent:next`
7. **Pipeline** detects the re-queue (gate rejection comment is newer than analysis comment), forces a fresh analysis

## Flow 3: Confidence Gate — Won't Do

1. **User** adds `agent:next` label
2. **Pipeline** analyzes the issue and determines no code changes are needed (bug already fixed, feature already exists, working as designed)
3. **Pipeline** posts analysis + `## 🚫 Analysis Gate: Won't Do` comment with reasoning
4. **Pipeline** swaps label to `agent:wont-do`, marks run as Completed
5. **User** can disagree: remove `agent:wont-do`, re-add `agent:next` to force re-analysis

## Flow 4: Quality Gate Failure with Draft PR

1. **Pipeline** generates code but quality gates fail (build errors, test failures, etc.)
2. **Pipeline** retries up to `maxRetries` times, giving the agent error feedback each time
3. If retries are exhausted, **pipeline** creates a **draft PR** with the failing code
4. **Pipeline** swaps label to `agent:error`
5. **User** can review the draft PR, fix issues manually, or close it

## Flow 5: Pipeline Error

1. **Pipeline** encounters an unrecoverable error (clone failure, timeout, provider error)
2. **Pipeline** swaps label to `agent:error`, records the failure reason
3. **User** can investigate via the web UI output log, fix the underlying issue, then remove `agent:error` and re-add `agent:next`

## Flow 6: PR Rework

1. **User** adds `agent:next` label to an issue that already has an open agent-created PR
2. **Pipeline** detects the existing PR by matching the branch name pattern (`feature/auto-{issueNumber}-*`)
3. **Pipeline** swaps label to `agent:in-progress`, enters rework mode
4. **Pipeline** checks out the existing PR branch and merges from main
5. **Pipeline** builds a rework prompt containing merge conflict info (if any) and/or PR review feedback
6. **Pipeline** re-runs code generation and quality gates using the rework prompt
7. **Pipeline** pushes to the existing branch (updates the PR automatically) and refreshes the PR body with current quality gate results
8. **Pipeline** adds `agent:done` label on success

If the user wants a fresh run instead of rework, they close the existing PR first, then add `agent:next`. The pipeline only enters rework mode when an open agent PR exists for the issue.

## Flow 7: Epic Decomposition

1. **User** adds `agent:epic` label to a high-level GitHub issue describing a feature or goal
2. **Pipeline** picks it up (in closed-loop mode when `DecompositionEnabled` is true on the template)
3. **Pipeline** swaps label to `agent:in-progress`
4. **Pipeline** explores the codebase, downloads open issues for deduplication, generates a decomposition plan, validates it via adversarial review
5. **Pipeline** posts the plan as a comment on the epic, swaps label to `agent:epic-review`
6. **User** reviews the proposed sub-issues on GitHub
7. **User** approves by swapping label to `agent:epic-approved`, OR requests changes by posting a comment and swapping back to `agent:epic`
8. **Pipeline** picks up the approved epic, generates full sub-issue descriptions, creates GitHub issues sequentially with dependency resolution
9. **Pipeline** posts a summary comment listing created sub-issues, swaps label to `agent:done`

If either phase fails, the label is swapped to `agent:error`. The user can retry by removing `agent:error` and adding `agent:epic` (re-runs Phase 1) or `agent:epic-approved` (re-runs Phase 2 only).

### Cross-Repository Epic Decomposition

When a **project** has an `EpicIssueProviderId` configured, the decomposition pipeline supports routing sub-issues to different repositories. The epic is polled from the project-level provider (e.g., a centralized tracker like Polarion or Jira), and each decomposed sub-issue can include a `targetRepository` field specifying which template's issue provider should receive it.

- If `targetRepository` resolves to a known template in the project → the issue is created in that template's issue provider
- If `targetRepository` is unresolvable or empty → the issue falls back to the dispatching template's issue provider
- Issues are created as regular issues (via `CreateIssueAsync`), not platform-specific sub-issues, ensuring compatibility across all issue providers

See [Projects — Cross-Repo Decomposition](projects.md#cross-repository-decomposition) for configuration details.

## Closed-Loop Mode

When the pipeline loop is active, it polls for `agent:next` issues automatically:

- Issues are processed FIFO (oldest `CreatedAt` first)
- Issues with `agent:error` or `agent:needs-refinement` are **skipped** (even if they also have `agent:next`)
- One issue is processed at a time; the loop waits for the current run to finish before starting the next
- Configurable poll interval, max runs per cycle, and backoff on failures
- When `DecompositionEnabled` is true on a template, the loop also polls for `agent:epic` and `agent:epic-approved` issues and dispatches them for decomposition
- When a project has an `EpicIssueProviderId` configured, the loop polls that provider for epics independently (see [Projects](projects.md))
