# PR Review Pipeline

The pipeline performs automated code review on pull requests using the same multi-agent infrastructure as the implementation pipeline, but in a read-only mode — agents analyze the diff and post findings without modifying code.

## How to Use

1. Add the `agent:next` label to any open pull request
2. The pipeline picks up the PR on the next poll cycle
3. The agent clones the repo, checks out the PR branch, and runs multi-agent code review
4. Review findings are posted as a PR review comment
5. The label transitions: `agent:next` → `agent:in-progress` → `agent:done` (or `agent:error` on failure)

Draft PRs are included in review dispatch (a warning is shown in the UI). To re-review after changes, remove `agent:done` and re-add `agent:next`.

## Workflow

```
Label PR with agent:next → Pipeline picks up PR → Clone → Checkout PR branch
  → [Brain sync] → Extract linked issues → Code review → Post findings → Done
```

Draft PRs are included in review dispatch (a warning is shown in the UI). To re-review after changes, remove `agent:done` and re-add `agent:next`.

### Rework Path and Draft PR Conversion

When a PR was created as a draft (e.g., after quality gate exhaustion) and a rework run completes successfully, the pipeline calls `UpdatePullRequestAsync` with `markReady: true` to convert the draft PR to ready-for-review automatically. No manual intervention is needed for this promotion.

### `agent:needs-refinement` on Implementation PRs

If you add `agent:next` for review on a PR whose linked issue carries `agent:needs-refinement`, the review pipeline runs normally. The `agent:needs-refinement` label on the issue does not block PR review dispatch — it only blocks re-dispatching the issue for implementation.

## Inline Review Comments

The pipeline posts code review findings as native inline comments on specific file:line positions in the diff, giving PR authors precise feedback at the exact location of each issue — in addition to the summary body comment.

### Configuration

Inline comments are enabled by default. Configure via the web UI under Settings → Global Defaults → Review ("Enable Inline Review Comments", "Minimum Severity for Inline Posting", "Maximum Inline Comments per Review"), or in pipeline config JSON:

```json
{
  "CodeReview": {
    "MaxIterations": 2,
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

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Enabled` | bool | `true` | Master switch. When false, body-only reviews are posted |
| `SeverityThreshold` | enum | `Warning` | Minimum severity for inline posting. Options: `Suggestion`, `Warning`, `Critical` |
| `MaxInlineComments` | int | `15` | Maximum inline comments per review (range: 1–50). Highest-severity findings are prioritized |
| `OrderBySeverity` | bool | `true` | Sort findings by severity (Critical first) when selecting which to post inline |
| `MaxRetries` | int | `1` | Times to re-ask the agent for structured output if it doesn't include file:line references (range: 0–5) |

### Behavior

- **When enabled**: Review agents output findings in `[SEVERITY] path/to/file.ext:LINE — message` format. The pipeline parses these, filters by severity threshold, caps at the configured limit, and posts them as inline comments via the Pull Request Reviews API.
- **When disabled**: A single body-level review comment is posted. No parsing or prompt enhancement occurs.
- **Graceful degradation**: If structured output parsing fails or the API rejects inline comments (HTTP 422), the pipeline falls back to body-only submission. Inline comments never fail the pipeline.
- **Findings without location**: Findings that don't reference a specific file:line appear only in the body summary.

## Template Configuration

Each pipeline job template has independent toggles:

| Property | Default | Effect |
|----------|---------|--------|
| `ImplementationEnabled` | `true` | Template processes issues for implementation |
| `ReviewEnabled` | `true` | Template processes PRs for code review |
| `DecompositionEnabled` | `false` | Template processes epics for decomposition |
| `HousekeepingEnabled` | `false` | Template manages agent:done PRs for branch updates, conflict rework, and stale branch cleanup |

Set `ReviewEnabled: false` to disable PR review for a template, or `ImplementationEnabled: false` to create a review-only template. See [Pipeline Orchestration](pipeline-orchestration.md) for the full technical reference.

## Acceptance Criteria Compliance

The pipeline runs an acceptance criteria compliance check in parallel with code reviewers during the implementation pipeline's code review phase. A dedicated agent evaluates whether the implementation satisfies the acceptance criteria from the original issue.

### How It Works

1. The AC agent reads issue context (`.agent/issue-context.md` or linked issue files) and the code changes
2. It produces a structured JSON report at `.agent/acceptance-criteria.json`
3. Non-compliant criteria are injected as `[CRITICAL]` findings into the fix prompt
4. The report is rendered in the PR body as a compliance table

### Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `acceptanceCriteriaEnabled` | bool | `true` | Enable/disable the compliance check |

The AC check runs on every review iteration. Non-compliant criteria are re-injected as `[CRITICAL]` findings into the fix prompt, and the compliance table in the PR body reflects the updated code state after each pass.

### Output Format

The agent writes `.agent/acceptance-criteria.json`:

```json
{
  "criteria": [
    {
      "criterion": "Description of the requirement",
      "status": "compliant|non_compliant|not_applicable",
      "evidence": "What satisfies this criterion",
      "reasoning": "What is missing (for non_compliant)"
    }
  ],
  "summary": "X of Y criteria addressed."
}
```

Status values: `compliant`, `non_compliant`, `not_applicable`.

### PR Body Rendering

The compliance report appears in the PR body as:

```
## Acceptance Criteria Compliance
| Status | Criterion | Notes |
|--------|-----------|-------|
| ✅ | Criterion text | Evidence |
| ❌ | Criterion text | Reasoning why it's not met |
| ⚠️ | Criterion text | Not applicable reasoning |
```

## Parallel Review Execution

Review agents execute in parallel when conditions are met, reducing review latency for multi-agent configurations.

### Conditions for Parallel Execution

Both must be true:
1. More than 1 review agent is configured
2. The agent provider supports parallel execution (`SupportsParallelExecution = true`)

Both Kiro CLI and OpenCode providers support parallel execution. When conditions aren't met, agents run sequentially.

### Isolation Model

All review agents unconditionally run in isolated sessions (`UseResume = false`) with no shared context from the code generation phase. This prevents self-attribution bias — a phenomenon where models evaluate their own output as more correct.

### Output Isolation

Each agent writes findings to a separate file: `.agent/review-findings-{agentName}.md`. Pre-computed diff artifacts (`.agent/diff-stat.txt`, `.agent/full-diff.txt`) are shared read-only across all agents.

### Failure Isolation

Individual agent failures are contained — if one agent crashes or times out, the remaining agents continue executing. Failed agents are logged and their findings are omitted from the review.

## Reviewer Configurations

Reviewer Configurations define per-stack review agents. They map label sets to groups of specialized reviewers, enabling different code review strategies per technology stack.

### Configuration Model

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Id` | string | auto-generated GUID | Unique identifier |
| `DisplayName` | string | (required) | Human-readable name |
| `MatchLabels` | string[] | `[]` | Labels for matching. Empty = matches all jobs unconditionally |
| `Agents` | ReviewAgent[] | (required) | Ordered list of review agents |
| `Enabled` | bool | `true` | Whether this config participates in resolution |
| `ExecutionOrder` | int | `0` | Ordering priority (lower = first) |

Each `ReviewAgent` has:
- `Name` — Agent display name (e.g., "Correctness", "SecurityReviewer")
- `Prompt` — The review prompt sent to the agent

### Resolution Logic

1. All enabled configs whose `MatchLabels` intersect with the job's labels are selected (ANY match)
2. Configs with empty `MatchLabels` always match (global fallback)
3. Results sorted by `ExecutionOrder` ascending, then `DisplayName` alphabetically
4. Agents from all matching configs are flattened into a single ordered list

### Default Configuration

When no custom reviewers are configured, the system uses two built-in configurations. **Default Reviewers** applies to every repository:
- **Correctness** — Logical correctness, edge cases, error handling
- **SecurityReviewer** — Security vulnerabilities, injection risks, auth issues
- **TestQualityReviewer** — Test coverage, test quality, assertion completeness

**.NET Reviewers** applies to repositories labelled `dotnet`:
- **DotNetSpecialist** — .NET-specific patterns, performance, API usage

Reset to defaults via Settings → Label Routing → Reviewer Configs → "Reset collection to defaults" (asks for confirmation).

### Project Review

Labels describe a repository's tech stack, so they cannot pick a reviewer that knows the product. A project with the project review on (Settings → Projects → *project* → Project Review, see [Projects — Project Review](projects.md#project-review)) adds its own reviewer to every code review of its repositories:

- **When:** in implementation runs (subject to `CodeReview.MaxIterations`, like the other reviewers) and in PR review runs. It is added after the label-matched reviewers and runs concurrently with them; its findings go through the same inline comments, retries and fix rounds. A project reviewer whose name another reviewer already has gets a number, as each reviewer writes its findings to a file named after it.
- **What it reads:** right before the review, the agent clones the project's other repositories (the enabled templates other than the run's own) into `.agent/project-repos/<template name>/`. The pipeline appends their list to the project reviewer's instructions, under "Project repositories". The other reviewers and the coding agent are not told about them.
- **What it is told:** the default instructions check the change against the rest of the project: contracts between the repositories (endpoints, schemas, events, configuration keys), breaking changes, duplicated rules that now disagree, the project's decisions, its documentation, and the lessons in the brain (`.brain/`). One brain can serve several projects, so the reviewer checks that a lesson concerns this project. It explores the connected MCP servers (for example the ticketing system or the wiki with the architecture decision records) and checks how current each source is before relying on it, as documentation is often outdated. A break this change can avoid is `[CRITICAL]`; a fix that belongs in another repository is `[WARNING]`, naming the repository and file, so fix rounds do not chase it. In a project with a single repository, the reviewer checks the change against the project's decisions, documentation and the brain's lessons only.
- **Read-only clones:** the clones are inside the agent's metadata directory, which is in the workspace's `.gitignore`, so they never show up in the diff or a commit. A GitHub App repository is cloned with a read-only token. A GitLab or personal-access-token repository is cloned with its own token, as tokens of those kinds cannot be narrowed; after the clone, the agent removes the token from the remote URL, `FETCH_HEAD` and the reflogs, and sets a push URL that is no repository, so a push fails. A clone that cannot be made read-only is removed. The token itself still reaches the agent pod, as the token of the run's own repository does, so with the project review on, a run holds the tokens of the project's other GitLab repositories as well: give them project access tokens with a short expiry.
- **Cost:** with the project review on, each code review clones the project's other repositories and runs one more reviewer per round.

