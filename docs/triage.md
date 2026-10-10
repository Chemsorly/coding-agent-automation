# Triage

Triage turns a problem report into a root cause analysis (RCA) and proposed fix issues. You describe what
happened and what you expected; an agent investigates with everything the project gives it (the code of every
repository, the project's MCP servers such as Grafana, SonarQube or Confluence, open issues and earlier
triages), writes down what it checked and what it found, and proposes 0–5 issue drafts. People review the
drafts in the app and create the issues they want.

Triage is its own run type (`Triage`). It runs as a Kubernetes Job like every other run, with the same agent
profiles, MCP servers, timeouts, RBAC and run page.

See also: [Issue Workflows — Flow 8](github-issue-workflows.md#flow-8-triage), [Configuration — Triage](configuration.md),
[Projects](projects.md), [API Reference — Triage](api-reference.md#triage-endpoints).

## Two Ways to Start a Triage

| | Operator triage (the Triage page) | Label triage (`agent:triage`) |
|---|---|---|
| Start | **Triage › New triage** in the app | Add `agent:triage` to an issue |
| Linked to an issue | No | Yes, the labelled issue |
| Needs | `operator` on the project, one enabled template | `TriageEnabled` on a template (see below) |
| Report goes to | The app only | The app and a comment on the issue |
| Re-run | **Re-run with feedback** in the app | In the app, or comment and set `agent:triage` again |

### Operator triage

The form asks for a title, what happened and what you expected (required), and optionally when (presets or a
range), the environment, where (a service, endpoint or page), the repository to start looking in, the version
that was running, links and IDs (trace IDs, dashboards, a failed run, a ticket) and what you already tried.

The side panel shows what the agent will use: the project's enabled repositories (the one that runs the job is
marked), the MCP servers merged into the run (agent profile and project, by name), the context sources and the
time limit. It also lists triages of the same project from the retention window whose titles share words with
yours, so you can open one instead of starting a duplicate.

The job runs on the "start looking in" repository's template when you pick one, otherwise on the project's first
enabled template by name ([Template Ordering](projects.md#template-ordering)). Nothing is posted to a tracker until
someone creates issues.

### Label triage

When a template has `TriageEnabled`, the loop polls its tracker for `agent:triage` issues. When the project has an
epic tracker (`EpicIssueProviderId`), the loop also polls it, and the project's first enabled template with
`TriageEnabled` (by name) runs those triages. So bugs can be reported in a repository's own tracker or in the
project's epic tracker; either way the agent reads every repository of the project.

Issues that also carry `agent:in-progress`, `agent:triage-review`, `agent:error` or `agent:done` are skipped.

## What the Agent Gets

- The executor's repository, and every other enabled repository of the project cloned read-only into `repos/`
  (the same mechanism as [project epics](epic-decomposition.md#epic-scope-repo-epics-and-project-epics)).
- The MCP servers of the agent profile and the project, project and repository steering, the brain (read-only)
  and the environment setup.
- `.agent/issue-context.md`: the issue with its newest 50 comments, or the operator's report.
- `.agent/triage-context.md`: the earlier attempts of this triage (verdict, summary, questions and the feedback
  given after each) and up to 30 other triages of the project from the retention window, for duplicate detection.
- `.agent/open-issues/`: the open issues of every enabled tracker of the project (`MaxOpenIssuesForContext` each).

The prompt asks the agent to check cheap, high-signal sources first (recent changes, existing issues and triages),
to form at least three hypotheses before testing them, to reproduce in the workspace without pushing, to cite
evidence for every claim, to prefer links and queries over pasted log lines, to never write to external systems,
and to answer `inconclusive` with questions instead of guessing.

## The Report

The agent writes `.agent/triage-result.json`. The pipeline reads it leniently (only a missing or unknown verdict
fails the run), validates it and renders everything else from it:

- **Verdict** — `cause_found`, `inconclusive`, `not_a_bug` or `duplicate`, with confidence and whether it was
  reproduced.
- **From symptom to cause** — the causal chain, each step with its evidence.
- **Evidence** — every claim with its source, query and link.
- **What the agent investigated** — the hypotheses (confirmed, ruled out, open), every check in order (where, for
  which hypothesis, result) and what it did not check and why. The agent writes this list; it lets you judge
  whether it looked in the right places. The page also counts the checks per source.
- **Questions** (inconclusive) and **issue drafts** (root fix, mitigation or prevention, each with a target
  repository).

When `TriageReviewEnabled` is on (default), a second agent session reviews the RCA adversarially (sources for every
claim, hypotheses really ruled out, no correlation presented as cause, no gaps in the chain, drafts trace to the
cause and duplicate no open issue, no secrets or personal data) and a refinement pass fixes its findings.

Deterministic checks run after the review: `cause_found` needs at least one investigated entry, at most 5 drafts
are kept, a draft with an unknown repository moves to the executor's repository with a warning, empty drafts are
dropped, an inconclusive result without questions gets a generic one, fields are cut to their size budgets, and a
result file over 100 KB fails the run.

For a label triage the pipeline posts (or updates) one comment on the issue with the same sections — the
investigated list and the drafts collapsed, values sanitized and known secrets masked, at most 60,000 characters —
and sets `agent:triage-review` for every verdict.

## Reviewing and Creating Issues

On the triage page, a user with `operator` on the triage's project can:

- **Edit a draft** — kind, target repository (an enabled repository of the project), title and body. Edited drafts
  are marked; **Reset** restores the agent's version. The review does not run again on edits.
- **Create issues** — select drafts, optionally **Queue for implementation** (`agent:next`), and create them. The
  API creates each issue in the tracker of the target repository's template, with `agent:generated`, a footer
  naming the triage and attempt, and sanitized, masked text. Each issue is recorded as soon as it exists; a
  per-triage lock and the record make retries and double clicks safe.
- **Re-run with feedback** (any state but running) or **Re-run with answers** (inconclusive) — starts a new attempt
  that reads the earlier attempts and your feedback.
- **Dismiss** with an optional reason. A dismissed triage keeps its history and can still be re-run.

For a label triage the app also updates the issue: creating issues posts a summary comment and sets `agent:done`;
dismissing posts a short comment and sets `agent:wont-do`; a re-run posts the feedback as a comment and sets
`agent:triage`.

Agents never create issues during a triage run; the hub refuses it.

## Labels

| Label | Meaning |
|---|---|
| `agent:triage` | Queued for a triage run (label triage) |
| `agent:triage-review` | The RCA is posted; the drafts wait for review in the app |

`agent:triage` → `agent:in-progress` → `agent:triage-review` → `agent:done` (issues created) or `agent:wont-do`
(dismissed), or back to `agent:triage` for another attempt. A failed run sets `agent:error`; a failed dispatch
returns the issue to `agent:triage`, never to `agent:next`.

## The Triage Page and History

**Triage** in the navigation lists the triages of the project selected in the switcher (global roles may choose all
projects): status, problem, source, verdict and confidence, created issues, tokens of the latest attempt and the
last change. Tabs: All, Need you (RCA ready or questions), Investigating, Done; a source filter and a title search.
Triages that need someone also appear on **Attention** and in its badge.

A Triage run's run page shows its own steps — Preparation (including *Downloading issues and triage history*),
Investigation, RCA Review (hidden when the review is off) and Finalization (*Reporting RCA*) — and links back to the
triage.

Triages are deleted `TriageRetentionDays` (default 30) days after their last change, unless an attempt is running.
Created issues and tracker comments stay.

## Permissions

Viewing a triage needs `readonly` on its project; starting, editing, creating, re-running and dismissing need
`operator`. The web checks the role against the project stored on the triage. The triage API is operator-tier
(master key); agent keys cannot reach it.

## Settings

| Setting | Where | Default |
|---|---|---|
| `TriageEnabled` | Template (Pipelines › template features) | off — gates only the `agent:triage` polling |
| `triageReviewEnabled` | Settings › Global defaults › Triage, overridable per project | on |
| `triageRetentionDays` | Settings › Global defaults › Triage (1–365) | 30 |

## Limits

- The agent's write access is limited by the prompt, not enforced: the executor repository has a write token (as for
  decomposition) and MCP tools are used read-only by instruction.
- Text posted to a tracker is sanitized and known secret values are masked, and the review checks for secrets and
  personal data, but this is not airtight. Prefer links over pasted log lines in reports, and keep sensitive
  details out of label triages on public trackers.
- Moving an operator triage to a tracker, triaging a failed run from its page, and links to the app in tracker
  comments are not supported yet.
