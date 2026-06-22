# Agent Feedback Loops & Consolidation

## Agent Feedback Loops

After every pipeline run, the agent provides structured feedback about what went well and what didn't. This feedback is grounded in external signals (compiler errors, test failures, retry history) rather than pure introspection.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for where feedback collection fits in the pipeline flow (ReflectingOnRun step).

### How It Works

- **Success path** — Feedback questions are appended to the existing reflection prompt (no extra agent call). The agent reports what caused retries, what context was missing, and what could be improved.
- **Failure path** — A dedicated 60-second agent call collects feedback after max retries are exhausted, before creating the draft PR.

### Feedback Schema

Each run produces a `RunFeedback` record with two sections:

- **Harness Feedback** — For the pipeline team: category label, stuck reason, missing context, missing capabilities, prompt issues, suggestions
- **Issue Feedback** — For the issue author: category label, description of what's wrong, affected files, human action needed

### Where Feedback Appears

- **Run detail modal** — Collapsible "Feedback" section showing all fields
- **GitHub issue** — If issue feedback has a description, a comment is posted with the `<!-- agent:issue-feedback -->` marker
- **Harness suggestions** — Accumulated feedback feeds into the consolidation loops (see below)

### Category Reuse

The feedback prompt includes previously-used category labels from the last 50 runs, encouraging the agent to reuse existing labels for clustering rather than inventing new ones each time.

## Consolidation Loops

Three maintenance loops that review accumulated state and produce improvements. All are manually triggered from the **Consolidation** page in the sidebar.

### Brain Consolidation (per template)

Dispatches an agent to prune, deduplicate, and organize the `.brain/` knowledge repository. Runs a 5-phase process:

1. **Orient** — Scan all files, build inventory
2. **Gather Signal** — Identify drift, duplicates, contradictions
3. **Research & Verify** — Check if referenced tools/libraries/versions are still current, validate external links, update outdated information
4. **Consolidate** — Merge duplicates, resolve contradictions, convert relative dates to absolute
5. **Prune** — Remove stale entries, clean up empty files

After the agent produces changes, an **adversarial review** pass evaluates the diff summary (`.agent/brain-consolidation-diff.md`). The discriminator checks for incorrectly removed entries, bad merges, contradictions, and inaccurate factual updates. If CRITICAL or WARNING findings are found, a refinement pass revises the `.brain/` files.

Changes are committed and pushed automatically. Git history provides rollback.

Configuration: `BrainConsolidationReviewEnabled` (default: `true`) controls whether the adversarial review runs.

### Refactoring Detection (per template)

Dispatches an agent to analyze the codebase holistically for architectural drift. Produces up to `MaxRefactoringProposals` (default: 3) GitHub issues with bounded refactoring proposals. Each issue includes:

- Summary of the problem
- Affected files
- Suggested approach
- Labels: `agent:generated`

The agent looks for: TODO comments, duplicated logic, naming inconsistencies, structural drift from incremental changes, and overly complex areas.

**Execution flow:**
1. Clone code repo (+ brain repo for architectural context if configured)
2. Run git hotspot analysis (frequently-changed files)
3. Query open `agent:generated` issues and recent open issues for deduplication context
4. Query closed refactoring issues (last 90 days) for outcome feedback — helps the agent learn from implemented/rejected history
5. Execute agent with refactoring detection prompt
6. **Adversarial review** — evaluates proposals for non-existent file paths, unsupported claims, scope exceeding single-agent capacity (>30 files), and bundled concerns. If CRITICAL/WARNING found, refinement re-generates proposals.
7. Create GitHub issues (capped at `MaxRefactoringProposals`)

Configuration: `RefactoringReviewEnabled` (default: `true`) controls the adversarial review step. `MaxRefactoringProposals` (default: 3, per-project overridable) caps both the prompt instruction and issue creation count.

### Harness Suggestions (global)

Analyzes accumulated `RunFeedback` from all pipeline runs to identify recurring patterns. Produces a JSON file (`config/pipeline/harness-suggestions.json`) with the top 3-5 improvement opportunities ranked by frequency and impact.

Each suggestion includes:
- Concrete, actionable text (what to change)
- Rationale (why, with references to specific feedback patterns)
- Frequency (how many runs contributed to this observation)

**Execution flow:**
1. Early exit if no feedback data
2. Write feedback JSON to workspace (`feedback-data.json`)
3. Calculate feedback count and success rate from the data
4. Execute agent to generate suggestions
5. **Write-to-file step** — a follow-up agent call serializes suggestions to `.agent/harness-suggestions-output.json` (enables stable file for review)
6. **Adversarial review** — evaluates suggestions against original feedback data. Checks for ungrounded suggestions, implausible frequency counts, and non-actionable advice.
7. Parse final suggestions and persist to `config/pipeline/harness-suggestions.json`

Configuration: `HarnessSuggestionsReviewEnabled` (default: `true`) controls the adversarial review step.

### Consolidation Queue

Consolidation jobs are dispatched via a queue (`ConsolidationQueueService`). The queue has no hard size limit, but enforces:

- **Time-based expiry:** Jobs queued longer than 24 hours are expired and transitioned to `Failed` status
- **Deduplication:** The same `RunId` cannot be enqueued twice
- **Dispatch retries:** Up to 5 retry attempts before permanent failure
- **Cancellation tracking:** Cancelled run IDs are retained for 5 minutes to prevent race conditions during dequeue

Expired jobs are tracked via the `consolidation.jobs.expired` OTel metric.

### Consolidation Page

The sidebar shows a "Consolidation" nav item with a badge count (new issues + suggestions since last visit). The page displays:

- Per-template cards with trigger buttons and last-run status
- Global harness suggestions section
- Run history table across all consolidation types
