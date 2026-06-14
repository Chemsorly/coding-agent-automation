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
