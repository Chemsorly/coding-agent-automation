# Design Decisions

Why the system behaves as it does where the code can't tell you. Each entry is a rule the owner decided, with the reason and the cost accepted. Entries state intent, not implementation: read the code for how it works.

**Agents:** read the [Invariants](#invariants) before you change behavior, and search this file for the area you touch. If a change would break a rule, say so in the PR instead of rewriting the rule. Don't edit a Rule or Why line unless the issue asks for it.

**Maintainers:** add or change an entry in the same PR that changes the policy. A changed decision replaces its entry; git history keeps the old text. Bugs go to GitHub issues, not here. Areas without a preference are listed under [Flexible areas](#flexible-areas).

**Fields:** **Rule** is what the system does or refuses. **Why** is the owner's reason. **Accepting** is the cost we live with. **Not** lists rejected alternatives. **Revisit when** is the trigger to reconsider. A **Gap** line marks a decision whose implementation falls short while the owner's choice is pending.

## Invariants

The rules a plausible change could break. Details are in the linked entries.

- Private keys never enter an agent pod; agents get short-lived tokens only. ([Token vending](#token-vending-private-keys-never-leave-orchestrator-or-api-containers-security-invariant))
- No agent pod receives the master key; each Job gets its own derived key, and an agent can resume only its own run. ([Agent keys](#hmac-key-derivation-for-agent-auth--intentional-simplicity))
- Agents can't set human-approval labels such as `agent:epic-approved`. ([Gated labels](#dispatchgatedlabels-extensible-set-for-human-approval-required-label-transitions))
- The web UI requires a signed-in user, and `admin` is only ever bound globally. ([Sign-in](#web-ui-sign-in-one-oidc-provider-roles-in-helm-values-no-user-database), [Project scope](#admin-is-global-only-project-bindings-scope-actions-and-visibility))
- Clones of a project's other repositories are read-only and never reach the diff or a commit. ([Project review](#project-review-one-project-reviewer-per-project-with-read-only-clones-of-the-projects-other-repositories))
- Only the API reads or writes PostgreSQL. ([Services](#only-the-api-owns-the-database-each-service-has-one-job))
- Every background loop runs on one elected leader; the API runs no loops. ([Loops](#dispatch-loop-belongs-in-a-leader-elected-controller-not-the-stateless-api))
- All images build from one commit and deploy together. Never reorder enum members in hub types or reuse a retired MessagePack key. ([Images](#agent-images-rebuild-with-the-control-plane--no-wire-skew-window), [MessagePack](#messagepack-int-ordinals-for-signalr--homogeneous-deployment-assumed))
- Configuration evolves append-only: new fields get defaults; no renames or repurposing. ([Schema](#no-schema-versioning--append-only-config-evolution-via-nullable-properties))
- Agent-produced JSON is read leniently; orchestrator JSON is written strictly. ([JSON](#dual-json-options-strict-write-default-lenient-read-lenient))
- Every new pipeline enum gets a JSON roundtrip test. ([Enum test](#enum-roundtrip-test-is-a-mandatory-invariant-for-new-pipeline-enums))
- The confidence gate fails closed. ([Confidence gate](#confidence-gate-is-intentionally-fail-closed))
- Critical-path failures are fatal, enrichment failures are not; new steps are non-fatal unless a later step needs their output. ([Partial failure](#partial-failure-contract-enrichment-steps-are-non-fatal-critical-path-steps-are-fatal))
- Exhausted retries end in a draft PR labeled `agent:error`. ([Draft PR](#draft-pr-is-the-retry-exhausted-fallback))
- `agent:done` means every step finished and post-PR CI passed. ([agent:done](#agentdone-ordering-full-pipeline--post-pr-ci-before-label-is-set))
- A label swap adds the new label before it removes the old one. ([Label swap](#label-swap-add-first-ordering-for-crash-safety))
- Reviewers run in isolated sessions. ([Isolated review](#code-review-always-uses-isolated-sessions-no-shared-mode))
- Durable agent output gets an adversarial review step with its own switch. ([Adversarial review](#adversarial-review-is-a-default-pattern-for-all-durable-agent-outputs))
- Only CRITICAL findings trigger another review round; non-compliant acceptance criteria count as CRITICAL. ([Review iteration](#code-review-iteration-critical-only-triggers-re-review-warnings-get-one-fix-pass), [Acceptance criteria](#noncompliant-acceptance-criteria-as-critical-hard-contract-retry-budget-bounds-cost))
- Dispatch order is Review, Decomposition, Implementation, Consolidation; no urgency crosses a tier. ([Dispatch priority](#dispatch-priority-static-ordering-review--decomposition--implementation--consolidation))
- A work item stores identity only; config is resolved at assignment, and a failed enrichment returns 503. ([Payload](#workitemspayload-config-snapshot-must-be-at-dispatch-time-not-enqueue-time))
- `AgentTimeout` is the only agent time limit; chat pods have their own. ([Timeouts](#agenttimeout-is-the-only-agent-time-limit))
- An agent pod exits 0 exactly when its run's outcome is recorded and never reports SIGTERM; pod retries belong to the Kubernetes Job. ([Agent Jobs](#agent-jobs-kubernetes-retries-pods-the-agent-records-outcomes))
- Merging is always a human action. ([Refactoring loop](#refactoring-consolidation-loop-autonomous-up-to-pr-creation-merge-is-human-gated))
- Epics need human approval between the plan and the sub-issues. ([Epics](#epic-decomposition-two-phase-with-human-gate))
- One enabled template is one repository and one tracker; an epic's tracker decides where its sub-issues go. ([1:1:1](#111-template-binding-one-repo-one-tracker-per-enabled-template--intentional-constraint), [Epic scope](#epic-scope-tracker-of-record-determines-sub-issue-routing--intentional-same-rationale-as-111))
- Every setting is read by code, shown on a settings page, documented and range-checked. ([Settings](#settings-are-read-offered-and-range-checked--one-record-four-scopes))
- Context reaches agents as workspace files, and agents return structured output as files. ([Files](#filesystem-as-context-workspace-files-are-the-context-delivery-mechanism))
- Runs only append to the brain; only consolidation rewrites it; read-only brains are never written. ([Brain](#brain-runs-append-consolidation-curates))
- Prompt structure (scope fences, calibration, verification) is not configurable. ([Prompts](#prompt-architecture-layered-composition-with-non-overridable-structural-guardrails))
- Nothing calibrates itself: feedback is collected and shown, and the operator decides. ([Feedback](#feedback-loop-data-collection-only-automated-calibration-explicitly-deferred))
- Conflicted PRs are reworked unless the issue is `agent:wont-do` or `agent:cancelled`; missing active-run data skips all rework that cycle. ([Conflict rework](#conflict-rework-re-queues-issues-regardless-of-current-label--only-abandonment-labels-block), [Fail-closed rework](#issuereworkservice-fail-closed-on-missing-active-run-data-is-intentional))
- Every pipeline progress line is also a structured log entry with the run ID. ([Telemetry](#telemetry-philosophy-instrument-every-decision-point-for-full-run-traceability))
- One operator, no RBAC. ([Target user](#target-user-single-operatorpower-user--no-rbac-for-now))

## Deployment and topology

### Kubernetes and PostgreSQL are the only deployment target
<!-- 2026-08-16 (spec 041) -->
**Rule:** The system runs only on Kubernetes, with agents as one-shot Jobs, and it requires PostgreSQL. The Docker Compose, in-memory and long-lived SignalR agent modes are gone.
**Why:** The Job model proved itself in production (clean pods, autoscaling, no stale state), and the other modes cost test effort without users. **Accepting:** no zero-dependency local mode.
**Not:** progressive modes for easier onboarding; persistent pull-model agents.
**Revisit when:** a target without Kubernetes becomes a real requirement.

### Agent Jobs: Kubernetes retries pods, the agent records outcomes
<!-- 2026-10-03 -->
**Rule:** An agent pod exits 0 exactly when its run's outcome is recorded (or the work item was already terminal), whatever the outcome, and never reports a status for SIGTERM. Any other exit leaves the retry to the Job: a drain, eviction, preemption or node loss (pod condition `DisruptionTarget`) is replaced without spending `backoffLimit`, while a crash or OOM kill counts against it (2 retries, each a full rerun within the same `AgentTimeout`). While the API gives no HTTP response, the agent waits instead of exiting. The JobController acts only on a Job's `Complete` or `Failed` condition, never on pod counters, and records a failed Job as `InfrastructureFailure` (never claimed), `Timeout` (`DeadlineExceeded`) or `ExitCodeFailure` (with the last pod's termination). Every final failure ends in `agent:error` for a human to requeue.
**Why:** In production the retry budget went to control-plane outages and nightly node drains instead of agent crashes, a drained run reported itself Cancelled, and a Job still inside its retry backoff was failed early. One owner per failure keeps recovery inside Kubernetes Job semantics. **Accepting:** after a hard node loss the replacement waits until the old pod is gone (the node returns or its node object is deleted); a deterministic OOM reruns up to three times; a rerun starts from scratch.
**Not:** app-level requeue of infrastructure failures; a remaining-budget check before a rerun; reporting SIGTERM as Cancelled.
**Revisit when:** reruns from scratch cost too much, or replacements wait long after node losses.

### Only the API owns the database; each service has one job
<!-- 2026-07-04; updated 2026-08-28 (specs 041–045) -->
**Rule:** The web app (UI and pipeline orchestration) has no database access. The API is the sole owner of PostgreSQL and of all agent-facing endpoints. The JobController runs reconciliation, and the Scheduler runs periodic work. Web and Scheduler change state only through the API.
**Why:** The UI must consume the API, not be the source of truth; dispatch and reconciliation need their own leader lease; scheduled maintenance is isolated from both.
**Not:** a Kubernetes operator with CRDs; one monolith.
**Revisit when:** a layer needs scaling it can't get, or the JobController and Scheduler should merge back for simplicity.

### EF migrations start from a baseline that reuses the newest original ID
<!-- 2026-10-04 -->
**Rule:** The first migration is the baseline (class `Baseline`, ID `20260930213632_DropConsolidationRuns`). Keep that ID and add new migrations on top. A database that stopped before it must first upgrade through v0.4.9 or v0.4.10; the API refuses it at startup.
**Why:** The 23 original migrations (~11,800 lines) were squashed. With the reused ID, a database that applied every original migration and a build from before the squash both see the history they expect, so nothing rewrites `__EFMigrationsHistory` and rolling back stays safe.
**Not:** a new baseline ID plus a startup step that rewrites the migration history (an older build would then re-run its first migration).
**Revisit when:** squashing again: reuse the newest migration's ID the same way.

### Dispatch loop belongs in a leader-elected controller, not the stateless API
<!-- 2026-09-12; includes the 2026-08-14 leader-gating decisions -->
**Rule:** Every background loop runs on exactly one elected leader: dispatch in the Scheduler, reconciliation in the JobController, maintenance sweeps triggered by the Scheduler, and the web app's pipeline loop only on its leader. The API holds no loops and no leases. Kubernetes Job creation stays in the API because that is where the RBAC lives.
**Why:** Loops on every replica break per-selector concurrency caps (seen live: 4 pods for a cap of 3) and fire side effects such as branch updates twice. The pipeline loop resumes after a restart, so every replica would start polling.
**Not:** gating only the calls that reach external APIs; dispatch on every API replica.
**Revisit when:** never for the principle.

### Agent images rebuild with the control plane — no wire skew window
<!-- 2026-08-22 -->
**Rule:** CI builds every image, control plane and agents, from the same commit in the same run.
**Why:** Agents and control plane share MessagePack wire types, so building them together removes version skew; a wire-contract snapshot test is unnecessary.
**Revisit when:** agent images are pinned or released separately; then add a wire-contract snapshot test.

### One agent image per agent tool, with every tech stack
<!-- 2026-10-04 -->
**Rule:** Each agent tool (Kiro, OpenCode, Claude Code) has one image, a target of `dockerfiles/agent.Dockerfile`, that carries every supported stack: .NET 10 SDK, JDK 21 + Maven and Python 3.12. Labels still pick the agent profile and job template; templates of different stacks point at the same image.
**Why:** The agent worker needs the .NET SDK in every image anyway, so per-stack images saved little, while nine near-copies of the Dockerfile drifted apart, CI built eighteen agent images per run, and no image could run a polyglot repository. **Accepting:** a larger image and a slower first pull on a new node, every stack's CVE findings in every agent image, and one version per stack.
**Not:** one image per agent tool and stack.
**Revisit when:** a stack needs two versions side by side (for example Java 17 and 21), or image size measurably slows pod start.

### MessagePack int ordinals for SignalR — homogeneous deployment assumed
<!-- 2026-07-04 -->
**Rule:** Hub messages use MessagePack with integer enum ordinals and numbered keys, so member order and key numbers are a wire contract. Never reorder enum members in hub types. A retired key stays tombstoned; never reuse its number.
**Why:** The orchestrator and agents always run the same build (one Helm release), so ordinals are safe. Stored data still carries retired keys, so a reused number would read old data as the new field. **Accepting:** no compile-time guard beyond the hub serialization tests.
**Not:** string enum serialization; explicit ordinal annotations.
**Revisit when:** rolling upgrades with mixed versions become a goal.

### Pod anti-affinity: soft (preferred) spreading, not hard (required)
<!-- 2026-08-28 -->
**Rule:** Control-plane Deployments with more than one replica spread over nodes with preferred (soft) anti-affinity.
**Why:** Hard anti-affinity leaves pods Pending forever on clusters with fewer nodes than replicas. The services are stateless, so co-location lowers availability but never corrupts state.
**Not:** required anti-affinity; none.
**Revisit when:** a deployment needs hard node isolation; the chart's `affinity` override covers that.

### Chat keepalive Redis: required for multi-replica deployments
<!-- 2026-08-28 -->
**Rule:** Redis is required when the API or the web app runs more than one replica. It holds chat heartbeats across replicas, as it already backs SignalR.
**Why:** Without it, a keepalive that lands on another replica is invisible to the watcher, and an active chat pod is killed after the idle timeout. **Accepting:** a single replica falls back to in-process heartbeats.
**Not:** Redis everywhere (breaks local dev); a separate on/off switch.
**Revisit when:** never for the principle.

## Security

### Token vending: private keys never leave orchestrator or API containers (security invariant)
<!-- 2026-07-04; updated 2026-09-26 -->
**Rule:** GitHub App private keys stay in the API, web and Scheduler processes. Agents get only short-lived installation tokens, which they request before each provider operation. No container that runs an AI agent holds a private key.
**Why:** A hijacked agent (prompt injection, hallucination, malicious input) must not reach secrets that allow lasting harm. **Accepting:** token refresh depends on the agent's SignalR connection. GitLab has no token vending, so the GitLab token reaches the agent; use project access tokens that expire within a day.
**Not:** long-lived credentials as Kubernetes Secrets; projected volumes.
**Revisit when:** never for the principle; move GitLab to short-lived tokens when they are available.

### HMAC key derivation for agent auth — intentional simplicity
<!-- 2026-07-04; updated 2026-09-26 -->
**Rule:** Each agent Job gets its own key, derived from the master key and the Job name and stored in a Secret for that Job. No pod receives the master key, so one pod's key can't impersonate another pod or the operator. An agent can resume only a run whose work item belongs to it.
**Why:** One master secret is simple to manage, and agents are ephemeral: identity matters for routing and logs, not for fine-grained access. **Accepting:** no per-agent revocation; a compromise means rotating the master key.
**Not:** an individual secret per agent; scoped master keys per label group.
**Revisit when:** a single agent must be revoked without rotating the master key.

### DispatchGatedLabels: extensible set for human-approval-required label transitions
<!-- 2026-08-14 -->
**Rule:** Agents can't set labels that mark a human approval (today only `agent:epic-approved`); the hub ignores such requests with a warning. Every new human-approval label joins this set.
**Why:** An agent must not escalate its own privileges by setting its own transition label.
**Not:** per-label gating config; webhook approval gates.
**Revisit when:** a human gate needs something other than a label, such as a UI action.

### Web UI sign-in: one OIDC provider, roles in Helm values, no user database
<!-- 2026-10-04 -->
**Rule:** Every web UI page requires a signed-in user, and sign-in can't be turned off. Users sign in through one OIDC identity provider or the local `admin` account. Three fixed roles (`readonly` < `operator` < `admin`) are bound to OIDC groups or users in the Helm values, globally or for one project.
**Why:** The UI controls agents that hold repository credentials. The identity provider owns users and groups and the chart owns who may do what, as in ArgoCD. **Accepting:** binding changes need a web pod restart; there is no record of who dispatched or cancelled what; logging out does not end the identity provider's session.
**Not:** an own user database; custom roles or policy lines; several identity providers at once; a switch that disables sign-in.
**Revisit when:** a team needs a permission between the three roles, or an audit trail becomes a requirement.

### admin is global-only; project bindings scope actions and visibility
<!-- 2026-10-04 -->
**Rule:** `admin` can only be bound globally. A project binding (`readonly` or `operator`) decides both what a user may do with the project's work and whose data the user sees: a user with only project bindings sees one project at a time and none of the cross-project pages. Agent Chat on a project needs `operator` there; a chat without a project needs global `operator`. Template changes, including the enable toggles, are admin-only.
**Why:** Templates and settings point at the global provider credentials, so a project admin could reach other projects' repositories. A chat on a project gets that project's MCP servers and secrets, the same exposure as dispatching work on it.
**Accepting:** a project team can't pause its own templates; a user bound to several projects switches between them.
**Not:** project admins; an "All projects" view across a user's projects.
**Revisit when:** templates stop sharing the global provider credentials.

## Dispatch and scheduling

### Dispatch priority: static ordering Review > Decomposition > Implementation > Consolidation
<!-- 2026-08-14; updated 2026-09-13 -->
**Rule:** Pending work dispatches by tier: Review, then Decomposition, then Implementation, then Consolidation. Within a tier, higher priority weight goes first (manual dispatch counts as higher), then the oldest. The order is fixed, not configurable.
**Why:** Review unblocks people waiting for feedback, one decomposition unblocks many implementation runs, implementation is background work, and consolidation is housekeeping. Round-robin made a review wait behind ten implementations. **Accepting:** lower tiers can starve; urgency never crosses a tier.
**Not:** configurable weights per run type; age-based promotion; round-robin; label-based priority.
**Revisit when:** several teams share the system and lower tiers visibly starve, or someone needs cross-tier urgency.

### MaxConcurrentDecompositions and MinIssueSlots: global-only
<!-- 2026-09-30 -->
**Rule:** The decomposition cap and the minimum issue slots are global; projects can't override them.
**Why:** One dispatcher allocates slots across all projects and has no per-project concurrency domain, so per-project values mean nothing. Keep it simple for now.
**Not:** per-project values applied to the global cap; per-project soft throttles.
**Revisit when:** one project measurably takes all decomposition slots; then add per-project quotas with sub-queues.

### MaxRunsPerCycle=0 (unlimited) is intentional — other mechanisms bound concurrency
<!-- 2026-07-04 -->
**Rule:** By default, a dispatch cycle has no cap on the number of runs it starts.
**Why:** Per-selector pod limits and the decomposition cap already bound concurrency; a per-cycle cap would only throttle the common case. Users who want a cap set one.
**Not:** a positive default as a safety net; a cap derived from cluster capacity.
**Revisit when:** unbounded dispatch causes scheduling pressure in the cluster.

### Dispatch 503: polling loop is the retry mechanism, no explicit handler needed
<!-- 2026-09-22 -->
**Rule:** When a dispatch attempt fails transiently (no free PVC, Kubernetes error), the work item stays Pending and the next loop cycle retries it. No retry headers, dead-letter queue or alert.
**Why:** The system runs in the background on a loop. Speed is not a constraint, and the next pass recovers a missed attempt.
**Not:** retry-after headers; dead-lettering after repeated 503s; an alert on the first 503.
**Revisit when:** items wait several cycles during sustained cluster trouble; then shorten the interval or add backoff.

### WorkItems.Payload: config snapshot must be at dispatch time, not enqueue time
<!-- 2026-08-29; 2026-08-31 -->
**Rule:** A work item stores only identity: issue, provider IDs, run, selector, timeout and trace context. Steering, provider configs with fresh tokens, quality-gate and reviewer configs, MCP servers and pipeline settings are resolved when the agent fetches its assignment. If that enrichment fails, the API returns 503 and the agent retries; it never gets an assignment without configs.
**Why:** Config can change between enqueue and start, and a snapshot silently ignores the change (seen in production: a steering update missed a run queued an hour earlier). An assignment without configs would run without quality gates or steering.
**Not:** a snapshot at enqueue; a re-fetch on every poll.
**Revisit when:** never for freshness; if assignment gets slow, add a short-lived server cache that keeps it.

### Circuit breaker is an infrastructure safeguard, not a provider health check
<!-- 2026-07-04 -->
**Rule:** The pipeline loop's circuit breaker trips only when every enabled template is failing at once, and it resumes after a cooldown. A single failing provider is skipped and backed off per template; it never trips the breaker.
**Why:** The breaker is for shared outages (network, DNS, gateway) that heal by themselves; per-template back-off handles one provider.
**Not:** per-template circuit breaking; no breaker.
**Revisit when:** several teams with separate infrastructure share the system.

### Agent label routing: layered resolution from provider to global default
<!-- 2026-07-04 -->
**Rule:** The repository provider's required labels choose which agent runs a job; without them, the global default labels apply.
**Why:** Repos can target different agent stacks (dotnet, python) without per-repo config when the default fits.
**Not:** template-only selectors; per-issue routing.
**Revisit when:** a third layer (per issue or per template) is needed.

## Pipeline behavior

### Confidence gate is intentionally fail-closed
<!-- 2026-07-04 -->
**Rule:** The analysis confidence gate treats unknown values as not ready, and any blocking issue forces not ready whatever the recommendation.
**Why:** The pipeline opens real PRs on real repos, and reviewer time is the bottleneck, so missing a valid run costs less than a broken PR. Analysis retries absorb transient model problems.
**Not:** fail-open with an early quality-gate check; strictness per project.
**Revisit when:** false negatives become a measurable drag (watch the `agent:needs-refinement` rate).

### Partial failure contract: enrichment steps are non-fatal, critical path steps are fatal
<!-- 2026-07-04 -->
**Rule:** Steps on the critical path (clone, branch, code generation, quality gates, PR) fail the run. Enrichment steps (brain sync, PR description, feedback, review posting) log a warning, and the run continues. A new step is non-fatal unless a later step consumes its output.
**Why:** Whether anything downstream needs the output decides it, which is simpler than per-step configuration.
**Not:** everything fatal; per-step fatality settings; retry first, then ignore.
**Revisit when:** a non-fatal step's silent failures cause consistent quality drops; then make it fatal.

### Cleanup step before PR is intentional quality polish
<!-- 2026-07-04; budget 2026-09-06 -->
**Rule:** After quality gates pass, a cleanup agent call removes debris from fix iterations (debug output, temporary files, verbose logging), and the gates run again. Cleanup shares the retry budget. Its prompt limits it to the changed files and about 10 tool calls; the budget is soft, not enforced.
**Why:** Passing tests are not enough for a reviewable PR. Without the budget, agents spent hours linting the whole codebase; strict lint and format checks belong in CI. **Accepting:** cleanup failures use the shared retry budget.
**Not:** no cleanup; advisory cleanup without re-validation; a separate retry budget; removing the tool-call budget.
**Revisit when:** cleanup rarely changes anything (skip it), or normal-size diffs keep exceeding the budget (raise it or sample files; don't remove it).

### Draft PR is the retry-exhausted fallback
<!-- 2026-07-04 -->
**Rule:** When quality gates still fail after all retries, the pipeline opens a draft PR with the work, labeled `agent:error`, instead of only failing the run.
**Why:** People see what the agent tried and can finish it without digging through logs.
**Not:** failing without a PR; a summary comment only.
**Revisit when:** abandoned draft PRs become a measurable housekeeping burden.

### agent:done ordering: full pipeline + post-PR CI before label is set
<!-- 2026-09-30 -->
**Rule:** `agent:done` is set only after every step, post-PR enrichment included, has finished and CI on the PR's head commit has passed. A ready-for-review PR can exist while enrichment still runs. A CI failure on the head commit after promotion makes the run an error.
**Why:** A reviewer who sees `agent:done` must be able to trust that CI is green.
**Not:** done at PR creation (the old behavior marked failing PRs done); done after CI but before enrichment.
**Revisit when:** enrichment gets slow enough that the delay confuses operators, or post-PR CI is expected to fail.

### PR creation: draft-first then finalize, hybrid template + agent narrative
<!-- 2026-07-25; description file 2026-08-29 -->
**Rule:** The pipeline opens a minimal draft PR early. It then finalizes the PR with a generated body (test results, changed files, review findings, acceptance-criteria table) and the agent's Summary and Approach. The agent writes the narrative to `.agent/pr-description.md`; the narrative never comes from the agent's stdout.
**Why:** The early draft makes progress visible, the template guarantees structure and metrics, and the narrative explains what and why. Stdout mixes in tool output and reasoning, which scrambled PR bodies in production. **Accepting:** without the file, the PR has only the generated part.
**Not:** fully agent-written bodies; template-only bodies; parsing stdout with markers.
**Revisit when:** reviewers find the narrative unhelpful; then drop it to save tokens.

### Label swap: add-first ordering for crash safety
<!-- 2026-07-04 -->
**Rule:** A label swap adds the new agent label first, then removes the others.
**Why:** An issue must never be left without a status label, because operators couldn't see it. GitHub has no atomic label update. A brief two-label state is acceptable: dedup prevents double dispatch, and a person can tell the state.
**Not:** remove first, then add; an external lock.
**Revisit when:** GitHub adds atomic label updates, or two-label states confuse operators.

### External CI re-push: workaround for GitHub Actions webhook unreliability
<!-- 2026-07-04 -->
**Rule:** When CI doesn't start for a pushed commit, the pipeline checks again and then pushes an empty commit to trigger it. Force-pushing agent feature branches is allowed.
**Why:** GitHub Actions drops webhooks, and a dropped one never recovers, so waiting doesn't help. An empty commit works with any CI that triggers on push. **Accepting:** empty commits in agent branches.
**Not:** longer timeouts; GitHub's workflow_dispatch API (not provider-agnostic); manual retry only.
**Revisit when:** webhook delivery becomes reliable, or CI moves to a provider with guaranteed events.

### MaxRetries=3 is an arbitrary but well-performing default
<!-- 2026-07-04 -->
**Rule:** The defaults are 3 quality-gate retries (4 attempts) and 2 analysis retries (3 attempts). Keep them until evidence says otherwise.
**Why:** They are not calibrated, but they work well in practice; each retry costs tokens, and the same errors tend to repeat.
**Not:** formal A/B tests at this scale; retry counts by error type.
**Revisit when:** exhausted-retry draft PRs become a measurable problem (tune from data), or token cost matters (lower to 2).

### Failure history may include transient-wait entries — accepted noise
<!-- 2026-09-06 -->
**Rule:** The failure history that the agent receives, and that draft PR bodies show, also records iterations that only waited on a transient error such as a rate limit.
**Why:** This is known noise, not design, but the impact is low: draft PRs are rare, and the transient-retry cap limits it to 10 entries. **Accepting:** some stale entries in feedback prompts.
**Not:** recording only real fix attempts; that is correct, but the change must keep the retry loop's ordering invariant, so it is deferred.
**Revisit when:** feedback quality measurably suffers, or the transient cap is removed.

### Acceptance criteria parsing: regex-based, intentionally simple and deterministic
<!-- 2026-07-25 -->
**Rule:** A regex extracts acceptance criteria from an `## Acceptance Criteria` section (checkbox or numbered items); no LLM parsing. Issues without the section fall back to the issue's goals.
**Why:** The format is standard across trackers, and deterministic parsing never invents criteria; it trades recall for precision.
**Not:** LLM extraction for unusual formats; looser patterns.
**Revisit when:** a tracker uses a format the regex can't handle, or missed criteria become measurable.

### NonCompliant acceptance criteria as CRITICAL: hard contract, retry budget bounds cost
<!-- 2026-07-25 -->
**Rule:** A non-compliant acceptance criterion becomes a CRITICAL finding that forces another fix attempt. If it is still non-compliant when the retries run out, the PR ships with that status visible.
**Why:** Acceptance criteria are both the Definition of Ready and the Definition of Done. An agent that is still sure after several rounds is valid signal: the criterion may be infeasible, or the evaluator wrong. **Accepting:** the retry budget bounds the cost of evaluator false negatives.
**Not:** downgrading to a warning; confidence thresholds.
**Revisit when:** correct implementations regularly exhaust their retries on evaluator false negatives.

### Code review always uses isolated sessions (no Shared mode)
<!-- 2026-07-04; Shared mode removed 2026-09 -->
**Rule:** Review agents always start in a fresh session without access to the coding conversation. No shared mode exists.
**Why:** Models rate their own output as more correct when they see their own reasoning. Fresh-context review catches more errors and lets reviewers run in parallel.
**Not:** shared sessions with a different model; a choice per issue; shared mode as an escape hatch.
**Revisit when:** research shows that context-aware review with debiasing beats isolation.

### Multi-agent code review: specialized reviewers run in parallel
<!-- 2026-07-04; defaults updated 2026-10-02 -->
**Rule:** Several reviewers, each with one focus, run in parallel. By default, correctness, security and test-quality reviewers check every repository, and stack specialists (the .NET reviewer) check repositories with the matching label. A project can add its own project reviewer. The reviewer set is configurable.
**Why:** A reviewer that focuses on one concern catches what a generalist misses.
**Not:** one thorough generalist; only two reviewers; six or more (diminishing returns, noisy consolidation).
**Revisit when:** parallel review becomes too expensive; then compare findings per reviewer to see which roles earn their cost.

### Code review iteration: CRITICAL-only triggers re-review, warnings get one fix pass
<!-- 2026-07-25 -->
**Rule:** CRITICAL findings are fixed and reviewed again. Warnings and suggestions get one fix pass (a TODO is fine), and then the review loop ends. Zero findings end it at once. A maximum iteration count caps the loop.
**Why:** Only real defects justify another full round across all reviewers. Warnings are simple, and the refactoring loop picks up TODOs later. **Accepting:** human review is the only check for a bug introduced while fixing a warning.
**Not:** re-review after warning fixes; pass/fail without severity.
**Revisit when:** warning fixes often introduce new bugs.

### Adversarial review is a default pattern for all durable agent outputs
<!-- 2026-07-04 -->
**Rule:** Every step where an agent produces durable output (committed code, created issues, brain changes, posted comments) gets an adversarial review step with its own switch. Ephemeral output such as logs and status messages doesn't.
**Why:** In practice the adversarial reviewer always finds something to correct, critical bugs included, and a separate context removes self-attribution bias.
**Not:** review for code only; a case-by-case choice per feature; no review for internal artifacts such as the brain.
**Revisit when:** token cost becomes prohibitive and the measurable gain drops.

### Epic decomposition: two-phase with human gate
<!-- 2026-07-25; sub-issue size 2026-08-14 -->
**Rule:** In phase 1 the agent explores, plans, has the plan reviewed adversarially, posts it on the epic and sets `agent:epic-review`. Phase 2 creates the sub-issues, and it starts only after a person sets `agent:epic-approved`. Each sub-issue is one agent run with one verification criterion and at most `MaxDecompositionSubIssueFiles` files (default 12).
**Why:** The human gate stops a bad plan from creating many issues that all fail. The limit of 12 rests on agents routinely handling changes of 10–15 files; it is a low-confidence estimate, not measured on sub-issues.
**Not:** auto-approval after a clean review (possible later); fully autonomous decomposition; a limit of 5 (fragments the work) or 20 and more (no evidence).
**Revisit when:** outcomes from 50 or more sub-issues show errors rising with file count, or auto-approval is built.

### Refactoring consolidation loop: autonomous up to PR creation, merge is human-gated
<!-- 2026-07-25 -->
**Rule:** The refactoring loop runs from proposal through review, issue creation, implementation and PR without human approval, and it may create 30 or more issues in one batch. Merging stays a human action.
**Why:** Adversarial review, wont-do tracking and `agent:needs-refinement` gate quality well enough before the merge review, and the merge is the human checkpoint.
**Not:** approval at issue creation; auto-merge for small issues.
**Revisit when:** merged loop PRs keep causing regressions, or more than 30% of proposals end as `agent:needs-refinement` or `agent:wont-do`.

### Refactoring auto-dispatch with dependency chains: acceptable for simple tasks only
<!-- 2026-07-25 -->
**Rule:** Agents may create issues that declare `Depends on #N` or `Blocked by #N` to order simple, mechanical refactoring; the adversarial reviewer checks them. Dependencies for architectural work go through the human-approved epic workflow.
**Why:** Each refactoring issue is small and low-risk, so a mechanical order is safe.
**Not:** human approval for every dependency; time-based auto-release.
**Revisit when:** a bad dependency blocks work for more than 24 hours unnoticed, or chains grow deeper than 3.

### Refactoring proposal quality bar
<!-- 2026-07-04 -->
**Rule:** A refactoring proposal becomes an issue only if it touches a hotspot (recent git activity), one agent can do it in one run (fewer than 30 files), and the evidence is concrete (files and pattern instances, not advice). The adversarial review enforces this.
**Why:** The bar must be high enough to avoid noise and low enough to catch real debt; outcome data (done vs. wont-do) should move it over time.
**Not:** fixed complexity or duplication thresholds; filtering by scope only; leaving calibration entirely to the operator.
**Revisit when:** more than 50% of proposals are wont-do over 90 days; the system is then too aggressive.

### Zero open-issue context on decomposition run: InfrastructureFailure, not agent failure
<!-- 2026-09-22; cause corrected 2026-09-25 -->
**Rule:** A decomposition run that downloaded zero open issues and produced no plan fails as an infrastructure failure, not as an agent failure.
**Why:** Without issue context the agent can't plan. The orchestrator is at fault, so the failure must not use up the agent's retry budget.
**Not:** a generic failure; an immediate retry on zero context (it could loop while the cause lasts).
**Revisit when:** infrastructure failure covers several causes that need different recovery.

### RequestGetIssue double-retry tier: intentional resilience
<!-- 2026-09-22; gap found 2026-09-25; fixed 2026-10-03 (#3279) -->
**Rule:** Provider calls that agents make through the hub get a second, longer retry tier on top of the provider's own short retry budget, capped at about two minutes.
**Why:** GitHub blips (short outages, rate-limit surges) outlast the inner budget. An outer tier gives a longer recovery window without inflating every call's inner retries.
**Not:** one tier with a longer inner budget; no outer tier.
**Revisit when:** GitHub becomes measurably more reliable, or the outer tier hides failures that should surface.

### Conflict rework re-queues issues regardless of current label — only abandonment labels block
<!-- 2026-09-21 -->
**Rule:** When an agent PR conflicts with main, its issue goes back to `agent:next` from any label, including `agent:done`, `agent:error` and `agent:needs-refinement`. Only `agent:wont-do` and `agent:cancelled` block it.
**Why:** An open conflicted PR always needs rework, whatever the issue label says; only an explicit human decision to abandon the issue stops it.
**Not:** blocking `agent:done`; blocking all non-terminal labels.
**Revisit when:** an `agent:done` issue is re-dispatched although its PR was already merged or closed; then also check the PR state.

### HousekeepingActiveLabels set: narrow by design — only live-work labels protect branches
<!-- 2026-09-21 -->
**Rule:** Stale-branch cleanup spares a branch whose issue has a queued or in-progress label: `agent:next`, `agent:in-progress`, `agent:epic`, `agent:epic-approved` or `agent:epic-review`. Done, error, needs-refinement, wont-do and cancelled labels don't protect a branch; an open PR does.
**Why:** Cleanup is for branches left over after a merge or an abandonment, so only labels that mean work is queued or running protect a branch.
**Not:** all non-terminal labels; the full label state machine.
**Revisit when:** a new active-work label is added; it joins the set if it means work is in progress.

### IssueReworkService: fail-closed on missing active-run data is intentional
<!-- 2026-09-22 -->
**Rule:** If housekeeping can't get the set of branches with active runs, it skips all conflict-rework swaps for that cycle.
**Why:** A swap onto an issue that already has a run causes a double dispatch and a stuck label state that needs manual repair. A one-cycle delay costs nothing because the loop retries; safety beats speed for background work.
**Not:** fail-open with partial data (a truncated list looks like "no active runs").
**Revisit when:** list failures block urgent rework for several cycles in a row; then accept recent last-known data.

### Housekeeping updates one branch at a time per repository
<!-- 2026-08-14 -->
**Rule:** The housekeeping concurrency limit defaults to 1: one branch update in flight per repository per poll tick.
**Why:** Housekeeping acts as a merge queue, and the work happens in CI, which runs serially; more updates in flight buy nothing. This is the right long-term default, not a cautious start.
**Not:** a higher default for parallel CI setups.
**Revisit when:** repositories with independent CI pipelines would really merge faster with parallel updates.

### GitHub mergeability mapping: correctness-driven, conservative null for unknown states
<!-- 2026-08-14 -->
**Rule:** For housekeeping slots, GitHub's `blocked` and `unstable` merge states, and every state without an explicit mapping, mean that CI is still running: the slot stays in flight. `clean`, `dirty`, `draft` and `has_hooks` free the slot.
**Why:** GitHub reports `blocked` for the whole CI run when required checks exist. Treating it as done would free the slot before CI starts and defeat the concurrency limit.
**Not:** mapping `blocked` or unknown states to done.
**Revisit when:** GitHub changes or adds merge states.

### AgentTimeout is the only agent time limit
<!-- 2026-08-29; 2026-09-06; 2026-09-21 -->
**Rule:** `AgentTimeout`, which projects can override, is the one wall-clock limit for work-item and consolidation agents. The stall monitor kills an agent after `AgentTimeout` of silence, and the Kubernetes deadline is `AgentTimeout` plus a fixed 60 seconds as a last-resort backstop. Chat pods are separate: they have their own maximum duration and idle timeout.
**Why:** Two settings for one concern drift apart; a Helm value once silently overrode per-project timeouts. The application kills first, and Kubernetes only catches a kill that failed.
**Not:** a separate Helm job timeout; a separate stall-kill timeout; a global-only timeout.
**Revisit when:** never for the single source. If a safety cap independent of `AgentTimeout` is needed, add it under its own clear name; if silence kills confuse operators, a separate stall-kill timeout becomes justified.

### AgentStallMonitor: StallPollInterval is intentionally not project-overridable
<!-- 2026-09-21 -->
**Rule:** How often the stall monitor polls agent health is global; how often it warns can be set per project.
**Why:** Poll frequency multiplies health calls across every active run of every project, which is a system resource concern. Warning cadence is a per-project UX choice.
**Revisit when:** a project needs a different poll frequency.

### AgentStallMonitor: stall warnings are flat (no escalation) — intentional observability signal
<!-- 2026-09-21 -->
**Rule:** Stall warnings repeat at Warning level in the same format until the kill, which logs separately at Error ("Forcefully terminating").
**Why:** Repeated warnings show that the chain is alive but silent, not that a kill is near; counting them in Grafana gives the stall duration. An Error before the kill would duplicate the kill's own Error.
**Not:** escalating the last warning; growing intervals.
**Revisit when:** operators can't tell slow agents from imminent kills; then add one "kill imminent" line, not escalation.

### SignalR reconnection: chat pods reconnect indefinitely
<!-- 2026-07-04; updated 2026-08-16 -->
**Rule:** Chat pods retry their SignalR connection forever, with exponential backoff capped at 120 seconds plus jitter. Work-item pods end with their job anyway.
**Why:** Chat pods use SignalR for their whole life and must recover when the web app or API restarts. The idle-kill circuit removes idle chat pods within about 90 seconds regardless, so endless reconnects don't leak cluster resources.
**Not:** self-termination after a set time.
**Revisit when:** chat pod cost during long outages becomes measurable; then cap reconnection for chat pods.

### Consolidation scheduling: manual-only for now, automated scheduling is future roadmap
<!-- 2026-07-04 -->
**Rule:** Brain consolidation and refactoring detection start only from the Consolidation page; no timer or event triggers them.
**Why:** It is enough for now, and it avoids spending agent budget at peak times. It is a work-in-progress state, not a permanent choice.
**Not:** a daily timer; a run after every success.
**Revisit when:** brain staleness becomes a measurable quality problem, or people forget to run consolidation. The first step would be a trigger after every N implementation runs.

## Agents and context

### Agent provider abstraction supports N backends as first-class citizens
<!-- 2026-07-04 -->
**Rule:** Kiro and OpenCode are both full agent backends. Kiro is the main development focus; OpenCode is maintained as a peer, not best effort. The provider abstraction allows more backends.
**Why:** Provider diversity enables competitive evaluation and model or runtime flexibility.
**Not:** a single backend; OpenCode only as a proof of extensibility.
**Revisit when:** a third backend is added, or keeping both creates a disproportionate test burden.

### OpenCode health monitoring: session-status polling, not OS process signals
<!-- 2026-09-21 -->
**Rule:** Kiro reports health from its OS process. OpenCode has no subprocess, so it reports health by polling session status. Process-death counts are therefore always 0 for OpenCode: that is expected, not a telemetry gap, and the session summary in its stall warnings is the equivalent signal.
**Why:** Both give the stall monitor actionable signal through the same health status, and SSE disconnects are normal, so they must not count as process deaths.
**Not:** treating an SSE disconnect as a process death.
**Revisit when:** OpenCode exposes a server-alive endpoint.

### Kiro token/cost telemetry: provider subscription limitation, not a permanent design choice
<!-- 2026-09-30 -->
**Rule:** Kiro runs record session count and elapsed time only. Token and cost values stay zero, and the UI hides those tiles and columns when there is no token data.
**Why:** The Amazon Q subscription doesn't expose token usage through the Kiro CLI. This is a provider limitation, not policy: capture usage as soon as it is exposed.
**Not:** estimating tokens from output or time (misleading); always showing N/A columns (clutter).
**Revisit when:** the Kiro CLI or Amazon Q exposes usage in a parseable form.

### Filesystem-as-context: workspace files are the context delivery mechanism
<!-- 2026-07-04 -->
**Rule:** The pipeline gives agents context as workspace files (`.agent/`, `.brain/`, steering files): prompts say what to do, and files say what the context is. Agents also return structured output as files, not on stdout.
**Why:** Inline context made prompts too large and caused escaping problems. Files can be scanned for prompt injection before the agent reads them, people can inspect them, and they survive crashes.
**Not:** inline prompt context; a hybrid split.
**Revisit when:** agents often fail to read the referenced files.

### Steering content: project vs. repo are complementary, not competing
<!-- 2026-07-04 -->
**Rule:** Project steering (team preferences, style, behavioral limits) and repository steering (architecture, dependencies, repo conventions) reach the agent as two separate parts with no precedence rule. The agent resolves any ambiguity from context.
**Why:** They cover different concerns, so they shouldn't conflict.
**Not:** repo steering overriding project steering; a merge with priority markers; one combined file.
**Revisit when:** real conflicts between the two confuse agents.

### Open issue context: cross-issue awareness to prevent conflicting parallel changes
<!-- 2026-07-04 -->
**Rule:** Agents get up to 50 open issues (configurable) as files, so they know about work in flight. Closed issues are not included.
**Why:** Without knowledge of sibling work, parallel agents make conflicting changes, especially within epics; the cap prevents context overload. **Accepting:** recently closed sibling issues are missing.
**Not:** no cross-issue context; closed issues included; epic siblings only.
**Revisit when:** agents still make conflicting changes, or recently closed context proves valuable.

### Brain: runs append, consolidation curates
<!-- 2026-07-04; 2026-07-25; read-only update 2026-09-29 -->
**Rule:** The brain is plain files in a git repository. Runs read it before they start and only append lessons afterwards, with source and citation tracking. Only consolidation merges, prunes and resolves contradictions, optionally with an adversarial review. A read-only brain is read but never written or consolidated; read-only can be set globally or per project, and a template can switch it on but not off. The feature is experimental.
**Why:** Appending is safe, unchecked growth hurts agents, and consolidation keeps the brain small; citation tracking guides pruning. Git gives history, rollback and human inspection. Read-only serves templates that should use shared knowledge without adding to it.
**Not:** a database store (loses git history and inspectability); expiry by age (drops rare but useful entries); periodic full resets; brain access per provider or per run.
**Revisit when:** the feature becomes stable, brain size measurably slows runs, or consolidation keeps finding nothing to change.

### Prompt architecture: layered composition with non-overridable structural guardrails
<!-- 2026-07-25 -->
**Rule:** Prompt content (focus areas, checklist items) can be overridden per project. The structure around it (scope fences, thoroughness and calibration footers, the verification clause) is fixed in code and not configurable.
**Why:** Users customize what the agent does; the pipeline controls how it behaves. The structural parts are research-backed behavioral guardrails, and the pipeline flow is strict by design. **Accepting:** prompt changes need a deploy.
**Not:** a template engine for everything; hot-reloaded prompt files; automatic prompt optimization.
**Revisit when:** never for the split; if several teams need different calibration, make only the calibration tunable.

### Prompt versioning: out of scope, scale doesn't justify the infrastructure
<!-- 2026-07-25 -->
**Rule:** Git history is the only version record for prompts, and changes apply to all runs on deploy. No version numbers, changelogs, A/B tests or evaluation pipelines.
**Why:** Prompts change rarely, adversarial review catches prompt regressions, a single operator can revert quickly, and no teams with different prompt needs exist.
**Not:** content hashes in telemetry; semantic versions on prompt constants.
**Revisit when:** several teams share the system, or a prompt regression took days to find because telemetry couldn't correlate it.

### Feedback loop: data collection only, automated calibration explicitly deferred
<!-- 2026-07-04; 2026-07-25 -->
**Rule:** Feedback from every run is collected, harness suggestions analyze it, and refactoring outcomes (done vs. wont-do) are tracked. Nothing adjusts itself: the operator sees the results and decides.
**Why:** No safe design for automated calibration exists, and premature automation compounds errors.
**Not:** auto-disabling proposals above a rejection rate; auto-creating issues from suggestions; closed-loop self-improvement.
**Revisit when:** the data shows stable patterns that a safe automatic rule could act on.

## Data and serialization

### Dual JSON options: strict write (Default), lenient read (Lenient)
<!-- 2026-07-04 -->
**Rule:** Data that the orchestrator controls is written in one strict, canonical format. Agent-produced JSON and user-edited configs are read case-insensitively. New reads of agent output use the lenient options; new writes of orchestrator state use the strict ones.
**Why:** The system is authoritative on what it writes but can't trust LLM formatting; lenient reads prevent data loss from casing differences.
**Not:** one set of options; strict parsing with error messages that teach the agent the format.
**Revisit when:** structured outputs guarantee the formatting.

### Enums in agent-produced JSON: snake_case names, always parseable
<!-- 2026-07-04 -->
**Rule:** Enum values that an LLM produces use snake_case names (`not_ready`, `non_compliant`); enums only the orchestrator writes keep PascalCase. Every enum value that can appear in agent JSON must parse. Whether an enum carries its own converter or relies on the global options is free.
**Why:** LLMs produce snake_case more reliably, and explicit names plus lenient parsing give two layers of safety.
**Not:** PascalCase or snake_case everywhere; one mandatory converter style.
**Revisit when:** a bug traces to a missing converter; then require self-annotation for agent-facing enums.

### Enum roundtrip test is a mandatory invariant for new pipeline enums
<!-- 2026-07-04 -->
**Rule:** Every new enum in the pipeline namespace gets a case in the enum JSON roundtrip test, which checks that every value serializes as a string and back.
**Why:** An enum that silently serializes as a number corrupts persisted data after a code change, and the test costs one line per enum.
**Not:** relying on property or integration tests.
**Revisit when:** a source generator can verify string serialization for all enums.

### No schema versioning — append-only config evolution via nullable properties
<!-- 2026-07-04 -->
**Rule:** Configuration has no version field. New properties get defaults. Renames and type changes are avoided: to change a meaning, add a new field and retire the old one. A removed field leaves its key retired, so stored data still loads. One-time data migrations are fine; version gating is not.
**Why:** The same build always writes and reads the data (the homogeneous deployment that MessagePack also relies on), and the system is still a work in progress, so a schema version is premature.
**Not:** a version field with migrations per version; JSON Schema validation at startup.
**Revisit when:** a stable 1.0 needs backward compatibility with old configs, or a breaking change is unavoidable.

## Configuration

### Settings are read, offered and range-checked — one record, four scopes
<!-- 2026-09-29 -->
**Rule:** Four rules for pipeline settings:
1. Every setting is read by code. A setting nothing reads is removed, not wired up, and its MessagePack key is retired.
2. Every setting has a field on a settings page and a row in `docs/configuration.md`, except four internal fields that are not settings. Tests enforce both.
3. A value outside its range is refused when global settings, a project or an import is saved. A stored project override outside its range is skipped with a warning, so it affects only its own setting.
4. The scopes stay as they are: global settings, project overrides, the pipeline job template (bindings, workflow switches, `BrainReadOnly`, housekeeping limit), the repository provider (labels, secrets, setup, steering, blacklist) and the label catalogs. The record is not split, and system settings are not moved to Helm.

A setting's limits are standard `[Range]` attributes, the one source for the API check, the resolver and the settings pages' input limits.
**Why:** A 2026-09-27 review found settings that did nothing, working settings without a field, limits that differed between pages, and one bad project override discarding all of the project's overrides. Code review settings now live on one Code Review page.
**Not:** a settings registry that generates the pages and the docs table (a large UI rewrite that loses the hand-tuned layouts); system settings in Helm (more restarts, no problem solved); project-level label routing (labels also select the agent profile, so it would need project context everywhere labels are resolved).
**Revisit when:** the guard tests or the hand-written pages become the main cost of adding a setting; then generate the pages from the attributes.

### Project review: one project reviewer per project, with read-only clones of the project's other repositories
<!-- 2026-10-02 -->
**Rule:** A project can turn on a project review. Its reviewer joins every code review of the project's repositories, in implementation runs and PR review runs, after the reviewers that the repository's labels pick. It shares their findings, inline comments and fix rounds. It reads read-only clones of the project's other repositories, which never reach the diff or a commit. It checks the change against the whole project: cross-repository contracts, decisions in the connected MCP servers, documentation and brain lessons for this project. A fix that belongs in another repository is a `[WARNING]`, so fix rounds don't chase it. The settings page edits one reviewer, and the project stores a list. Project epics use the same clones.
**Why:** Repository labels choose reviewers, and they describe the tech stack: the same labels pick the agent image and the quality gates. A reviewer that knows the product doesn't fit them. **Accepting:** a GitLab or personal-access-token repository keeps its own token for the clone. The agent strips the token from the clone, blocks pushes and deletes a clone it can't make read-only, but the run still holds the tokens of the project's other GitLab repositories. Use short-lived project access tokens.
**Not:** "product labels" on repositories (labels route agent images, and a label without a matching agent profile blocks dispatch); a shared reviewer catalog (project instructions are project-specific); letting a project pick label reviewer sets (adds no project knowledge).
**Revisit when:** projects need several distinct reviewers in the UI, or GitLab tokens can be narrowed to read-only.

### Project overrides: nested settings deep-merge
<!-- 2026-07-04; 2026-07-25 -->
**Rule:** A project override of a nested settings object replaces only the sub-settings it sets; the others keep their global values. A scalar override applies when it is set.
**Why:** Replacing the whole object would silently reset unrelated sub-settings.
**Not:** replacing the whole object; merge behavior configurable per object.
**Revisit when:** another nested object becomes overridable; a guard test forces it through the same merge.

### 1:1:1 template binding: one repo, one tracker per enabled template — intentional constraint
<!-- 2026-09-30 -->
**Rule:** Each enabled template is bound to exactly one repository and one issue tracker, and no two enabled templates share either. Disabled templates are exempt. Saving a template enforces this.
**Why:** Issue, template and PR stay one-to-one, so no agent has to decide what to implement where; that is out of scope. Multi-repo work goes through project epics, which dispatch to single-repo templates.
**Not:** multi-repo templates; shared trackers with multiplexing; no enforcement (duplicate dispatch).
**Revisit when:** project epics can't serve a multi-repo case; then add a new multi-repo concept instead of relaxing this rule.

### Epic scope: tracker-of-record determines sub-issue routing — intentional, same rationale as 1:1:1
<!-- 2026-09-30 -->
**Rule:** An epic's tracker decides where its sub-issues may go. An epic in the project's epic tracker gets full project context and can route to any repository in the project. An epic in a template's own tracker stays in that tracker. No flag grants cross-repo routing; move the epic instead. The scope comes from the live config when the work is claimed.
**Why:** Routing ownership stays unambiguous, for the same reason as 1:1:1.
**Not:** an opt-in cross-repo flag for template epics; the dispatching template's scope.
**Revisit when:** an epic in a repository tracker needs cross-repo routing without the project tracker; then add an explicit mechanism, and don't loosen the tracker check.

## Observability

### Telemetry philosophy: instrument every decision point for full run traceability
<!-- 2026-07-04; 2026-08-29; 2026-08-31 -->
**Rule:** Every step an agent took must be traceable in Grafana. Anything slow or branching gets a span, and operationally relevant events get counters. Every pipeline progress line is also a structured log entry with the run ID. Trace context flows both ways on the orchestration channel, but not into provider API calls.
**Why:** The goal is debugging ("the system did A, I wanted B: find out why in Grafana"), not just alerting. Progress lines that exist only in the UI are invisible to ops tooling.
**Not:** job-level counters only; instrumenting only the critical path; a span event per output line; automatic trace propagation on every HTTP client.
**Revisit when:** telemetry storage costs more than its value. Lower trace sampling before you remove spans, and sample high-volume output lines if log cost grows.

### ExternalCiDuration vs PostPrCiDuration: two separate histograms for two distinct CI poll phases
<!-- 2026-08-28 -->
**Rule:** Pre-PR CI (on branch push) and post-PR CI (on the pull_request event after promotion) each have their own duration histogram. A new CI poll phase also gets its own histogram.
**Why:** One histogram for both doubles the samples on runs that go through both phases and inflates p50 and p99 dashboards.
**Not:** one metric with a phase tag (equally valid, but every panel needs a filter); one total per run (loses per-phase detail).
**Revisit when:** never for the separation.

### Grafana Faro RUM: CDN-only is acceptable for now — air-gapped deployments need revision
<!-- 2026-08-29 -->
**Rule:** The Faro browser SDK loads from unpkg.com with pinned integrity hashes. Without internet access it silently stays off, and the app is unaffected. An empty collector URL turns it off completely.
**Why:** The deployment target (Grafana Cloud with internet access) needs nothing more. **Accepting:** air-gapped use needs a code change to self-host the bundles, and a future Content-Security-Policy must allow unpkg.com in `script-src`.
**Not:** configurable bundle URLs (no demand yet); bundling with npm at build time (no JS build step).
**Revisit when:** Faro is needed without internet access; the minimal change is SDK and tracing URL overrides.

## Scope and users

### Target user: single operator/power-user — no RBAC for now
<!-- 2026-07-04 -->
**Rule:** The UI serves one expert operator who configures, watches and manages the pipeline. Work is submitted with tracker labels, outside the UI. No roles or permissions exist, and the UX favors expertise (compact, dense) over approachability.
**Why:** One person owns the whole pipeline lifecycle, so RBAC isn't needed yet.
**Not:** a developer-facing tool with guided flows; admin and viewer personas.
**Revisit when:** a second person needs access, or the system runs as a shared service; then add ArgoCD-style Read, ReadWrite and Admin roles.

### Image extraction: security hardening kept but feature is experimental, may be reworked
<!-- 2026-07-25 -->
**Rule:** Downloading issue images for agents keeps its security hardening (SSRF protection, magic-byte, dimension and byte-budget checks). The feature is experimental and may be reworked, and its limits are reasonable defaults, not invariants from a threat model.
**Why:** An agent recommended the hardening; it costs nothing in normal use and blocks real attacks through issue markdown, so removing it would be a regression.
**Not:** no image support; unsecured downloads.
**Revisit when:** the feature is tested and promoted (then formalize the limits), or the hardening causes problems.

## UX

### Visual design: dark-first, light theme exists for accessibility
<!-- 2026-07-04; confirmed after the redesign 2026-10-02 -->
**Rule:** Dark is the default theme and the design priority. The light theme must work but isn't hand-tuned, so verify new UI in dark.
**Why:** Developer tools are mostly dark-first, and equal polish doubles the design work. **Accepting:** the light theme gets less care.
**Not:** equal polish for both themes; no light theme (hurts accessibility).
**Revisit when:** users report specific light-theme problems.

### Interaction model: mouse-first with keyboard as bonus layer
<!-- 2026-07-04; confirmed after the redesign 2026-10-02 -->
**Rule:** The UI is mouse-first. Keyboard shortcuts are an optional power-user layer. Basic keyboard operation (tab order, visible focus) should work, but don't invest in keyboard-first navigation unasked.
**Why:** The owner works mouse-first, and the shortcuts were an agent's addition, not a design principle.
**Not:** keyboard-first design; removing the shortcuts.
**Revisit when:** users rely on keyboard navigation, or an accessibility audit requires more.

### Feedback messages: errors stay, successes fade, toggles offer undo
<!-- 2026-07-04; confirmed after the redesign 2026-10-02 -->
**Rule:** Error messages never dismiss themselves and have a manual dismiss button. Success messages disappear after about 3 seconds. Every toggle shows an undo snackbar, whether or not the loop is running.
**Why:** Errors are failures the user must acknowledge, while successes only confirm. Consistent undo is a cheap safety net; showing it only while the loop ran was accidental.
**Not:** auto-dismissed errors; a toast queue; undo only while the loop runs; no undo.
**Revisit when:** errors stack up confusingly (then show the latest and make older ones expandable), or the snackbar annoys when idle.

### Pipelines page is configure + dispatch only
<!-- 2026-07-04; confirmed after the redesign 2026-10-02 -->
**Rule:** The Pipelines page (`/pipelines`, formerly Agent Coding) only configures templates and dispatches work. It shows no run progress, output or summaries; those belong on the run pages.
**Why:** Agents run pipelines remotely, so a local progress view on this page never worked, and it blurs the page's job.
**Not:** a quick-glance progress view on this page.
**Revisit when:** never; the page's responsibility is clear.

## Flexible areas

The owner has no preference in these areas. Follow best practice and the surrounding code.

- Value types, class decomposition and DI wiring: follow DDD and .NET conventions.
- Error style at new dispatch entry points (throw or return a result): match the method's existing contract.
- Phase 2 of the `ProviderConfigId` value-type migration: do it when a natural opportunity comes up.
- Orphaned-run reconciliation: use set-based updates and rethrow cancellation like the other sweeps when you next touch it.
- Wrapper types for user-controlled strings such as selectors: follow the `IssueIdentifier` pattern for new ones.
- How the agent reconnect race, completed-job status posts and the active-Job count are handled: only the outcome matters (in-flight runs survive an API restart, each completed Job's status is posted once, and finished Jobs don't count as active).
- Agent registry cache staleness: acceptable while dispatch reads fresh data.
- Work-item payload schema detection: add a version field at the next breaking payload change.
- Chat idle-kill self-await: may be fixed by deleting the pod directly, as long as cleanup stays idempotent.
- A startup warning when chat heartbeats run without Redis: optional, for consistency.
- The advisory lock on the agent-synchronous dispatch path: add it if concurrent calls are possible there.
- Where `PvcPoolExhaustions` is emitted: keep it consistent with its documented meaning.
