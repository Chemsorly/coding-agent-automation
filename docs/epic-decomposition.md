# Epic Decomposition Pipeline

The pipeline can decompose high-level epics into implementation-ready sub-issues. This bridges the gap between broad goals and the atomic issues the implementation pipeline requires.

## How to Use

1. **Label an epic** — Add the `agent:epic` label to a GitHub issue describing a high-level feature or goal
2. **Phase 1 (Analysis)** — The pipeline picks up the epic, explores the codebase, and posts a decomposition plan as a comment on the epic
3. **Review the plan** — The epic transitions to `agent:epic-review`. Review the proposed sub-issues on GitHub
4. **Approve or reject** — To approve, swap the label to `agent:epic-approved`. To request changes, post a comment with feedback and swap back to `agent:epic`
5. **Phase 2 (Creation)** — After approval, the pipeline creates implementation-ready sub-issues with dependencies resolved

## Two-Phase Workflow

```
Label epic with agent:epic → Phase 1: Clone → Brain sync → Download open issues
  → Agent explores codebase → Adversarial review → Post plan comment
  → Label: agent:epic-review (awaiting human approval)

Approve: swap to agent:epic-approved → Phase 2: Clone → Brain sync
  → Agent generates sub-issue JSON → Parse & validate → Create issues sequentially
  → Post summary comment → Label: agent:done
```

## Label State Machine

| Current Label | Trigger | Next Label |
|---------------|---------|------------|
| `agent:epic` | Phase 1 dispatched | `agent:in-progress` |
| `agent:in-progress` | Phase 1 success | `agent:epic-review` |
| `agent:in-progress` | Phase 1/2 failure | `agent:error` |
| `agent:epic-review` | User approves | `agent:epic-approved` |
| `agent:epic-review` | User requests re-analysis | `agent:epic` |
| `agent:epic-approved` | Phase 2 dispatched | `agent:in-progress` |
| `agent:in-progress` | Phase 2 success | `agent:done` |
| `agent:error` | User retries Phase 1 | `agent:epic` |
| `agent:error` | User retries Phase 2 | `agent:epic-approved` |

## Approval Process

After Phase 1 posts the decomposition plan:

- **Approve**: Remove `agent:epic-review`, add `agent:epic-approved` → Phase 2 runs automatically
- **Request changes**: Post a comment on the epic with feedback, then remove `agent:epic-review` and add `agent:epic` → Phase 1 re-runs with your feedback as context
- **The plan comment is updated** (not duplicated) on re-runs, identified by the `<!-- agent:decomposition-plan -->` marker

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DecompositionEnabled` | `bool` | `false` | Enable decomposition polling for this template |
| `MaxDecompositionSubIssues` | `int` | `10` | Maximum sub-issues per epic (range: 1–20) |
| `MaxConcurrentDecompositions` | `int` | `2` | Maximum simultaneous decomposition runs |
| `DecompositionTimeout` | `TimeSpan` | `15 min` | Timeout for each decomposition phase |
| `MaxOpenIssuesForContext` | `int` | `50` | Open issues downloaded for deduplication context |

Example template configuration:
```json
{
  "Name": "Full Pipeline with Decomposition",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": true,
  "DecompositionEnabled": true
}
```

See [Pipeline Orchestration — Epic Decomposition](pipeline-orchestration.md#epic-decomposition-pipeline) for the full technical reference.
