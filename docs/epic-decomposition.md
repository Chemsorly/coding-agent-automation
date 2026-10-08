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

Approve: swap to agent:epic-approved → Phase 2: Clone → Brain sync → Download open issues
  → Agent generates sub-issue JSON → Parse & validate → Create issues sequentially
  → Post summary comment → Label: agent:done
```

> **Context for the agent:** Both phases download existing issues for deduplication context. In addition to open issues (up to `MaxOpenIssuesForContext`), decomposition runs also include recently-closed sibling issues (up to ~25% of the context budget, lookback 30 days). This helps the agent avoid creating sub-issues that duplicate recently completed work.

## Epic Scope: Repo Epics and Project Epics

Every epic follows the same two-phase workflow. The only difference is its scope, and the scope comes from the tracker the epic lives in:

| | Repo epic | Project epic |
|---|---|---|
| Lives in | The tracker of a template with `DecompositionEnabled` | The project's epic tracker (`EpicIssueProviderId`) |
| May create sub-issues in | That template's tracker only | The tracker of every enabled template in the project |
| Workspace | The template's repository | The executor's repository, plus the other enabled project repositories, cloned read-only into `repos/` |
| Runs with the settings of | Its own template | The executor: the first enabled template with `DecompositionEnabled`, by template name (see [Template Ordering](projects.md#template-ordering)). A manual dispatch from the epic drawer uses the drawer's template |

- The run is bound to the tracker the epic lives in: the plan, the summary, the labels and the dedupe check all use the epic itself.
- A project epic's sub-issue without a matching `targetRepository` goes to the executor's tracker. It is never created in the epic tracker, unless the epic tracker is also the executor's tracker. If the executor is missing from the project's repository list (for example because an earlier template has the same name), such a sub-issue is not created.
- Template names are the routing keys, so they must be unique within a project (see [Template Rules](projects.md#template-rules)). A template saved before that rule, whose name is empty or already used by an earlier template in the project, is left out of the repository list.
- The other repositories get none of their secrets or setup steps. A GitHub App repository gets a read-only token. Any other (GitLab, a personal access token) keeps its own token; the agent removes it from the clone and turns pushing off there (see [PR Review — Project Review](pr-review.md#project-review)).
- The scope is decided again when the agent picks up the job, from the configuration at that time.
- If the epic tracker is also a template's tracker, its epics are project epics, and each epic is queued once, with the executor. An epic tracker belongs to one project; if two projects share one, the first by name owns it.
- Project epics wait in the executor's queue next to its repo epics, oldest first, in the same round-robin as all other templates. While the executor is not polled (for example because it is rate-limited), project epics wait.
- The open-issue context for deduplication comes from the tracker the epic lives in. For a project epic that is the epic tracker, not the repository trackers, so a rerun of Phase 2 does not see the sub-issues an earlier, partly failed run already created.
- Any project can have an epic tracker, including the Default project. Templates moved into a project (for example into Default, when their project is deleted) widen what its project epics can reach.

See [Projects — Multi-Repo](projects.md#use-case-multi-repo-cross-repo-decomposition) for a cross-repo setup.

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
| `MaxDecompositionSubIssueFiles` | `int` | `12` | Maximum files a single sub-issue may create or modify (range: 1–30). Keeps each sub-issue within single-agent capacity |
| `MaxConcurrentDecompositions` | `int` | `2` | Maximum simultaneous decomposition runs |
| `MaxOpenIssuesForContext` | `int` | `50` | Open issues downloaded for deduplication context |

Decomposition has no timeout of its own: each agent call, including the adversarial review, runs with `AgentTimeout`, like every other agent call.

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
