# PR Review Pipeline

The pipeline performs automated code review on pull requests using the same multi-agent infrastructure as the implementation pipeline, but in a read-only mode — agents analyze the diff and post findings without modifying code.

## How to Use

1. Add the `agent:next` label to any open pull request
2. The pipeline picks up the PR on the next poll cycle
3. The agent clones the repo, checks out the PR branch, and runs multi-agent code review
4. Review findings are posted as a PR review comment
5. The label transitions: `agent:next` → `agent:in-progress` → `agent:done` (or `agent:error`)

## Workflow

```
Label PR with agent:next → Pipeline picks up PR → Clone → Checkout PR branch
  → [Brain sync] → Extract linked issues → Code review → Post findings → Done
```

Draft PRs are included in review dispatch (a warning is shown in the UI). To re-review after changes, remove `agent:done` and re-add `agent:next`.

## Inline Review Comments

The pipeline posts code review findings as native inline comments on specific file:line positions in the diff, giving PR authors precise feedback at the exact location of each issue — in addition to the summary body comment.

### Configuration

Inline comments are enabled by default. Configure via the web UI under Settings → Quality Gates → Code Review → Inline Review Comments, or in pipeline config JSON:

```json
{
  "CodeReview": {
    "MaxIterations": 2,
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

The AC step runs only on the first review iteration. Results are not re-evaluated after fixes — the quality gates (build + tests) validate correctness on subsequent iterations.

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

All three must be true:
1. `ReviewIsolation` is `Isolated` (default)
2. More than 1 review agent is configured
3. The agent provider supports parallel execution (`SupportsParallelExecution = true`)

Both Kiro CLI and OpenCode providers support parallel execution. When conditions aren't met, agents run sequentially.

### Isolation Model

| Mode | Behavior | Use Case |
|------|----------|----------|
| `Isolated` (default) | Each review agent runs in a fresh session with no shared context (`UseResume = false`) | Prevents self-attribution bias |
| `Shared` | Review agents share the code generation session (`UseResume = true`) | Legacy behavior |

### Output Isolation

Each agent writes findings to a separate file: `.agent/review-findings-{agentName}.txt`. Pre-computed diff artifacts (`.agent/diff-stat.txt`, `.agent/full-diff.patch`) are shared read-only across all agents.

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

When no custom reviewers are configured, the system uses four built-in agents:
- **Correctness** — Logical correctness, edge cases, error handling
- **DotNetSpecialist** — .NET-specific patterns, performance, API usage
- **SecurityReviewer** — Security vulnerabilities, injection risks, auth issues
- **TestQualityReviewer** — Test coverage, test quality, assertion completeness

Reset to defaults via Settings → Label Routing → Reviewer Configs → "Reset to Defaults".

