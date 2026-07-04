# Design Decisions

Human-authored intent behind non-obvious design choices. This file is the authoritative source for "why" questions that can't be inferred from code or genre patterns alone. Generated via the intent-extraction hook.

**Usage:** Agents MUST read this file before proposing changes to understand constraints and deliberate choices. If a decision here contradicts what seems "obvious," the decision wins — the human made it for a reason.

<!-- Intent Extraction Sessions -->
<!-- Session: 6 | Last run: 2026-07-04 | Decisions captured: 37 -->
<!-- Queued for next session: none — run again after significant code changes -->

---

<!-- Decisions are grouped by category, alphabetical within each group. -->
<!-- Categories: architecture | scope | configuration | ux | integration | future-direction -->

## Architecture

<!-- Decisions about system structure, patterns, and component boundaries -->

### Monolithic orchestrator is intentional (for now)

**Date:** 2026-07-04
**Category:** architecture

**Decision:** We keep all dispatch logic, reconciliation, leader election, and lifecycle management inside the single web application process (Blazor UI + orchestration + dispatch in one binary). Splitting into a standalone operator/controller is on the roadmap but not yet justified by scale.

**Context:** Spec 036 explored a standalone CRD controller, but spec 035 (Postgres-based work distribution) was implemented instead, keeping everything in-process. Comparable systems (Argo Workflows, Tekton, Flux) separate controllers from UIs, but this system isn't at a scale where independent scaling of the orchestration layer is needed. Leader election handles multi-replica safety today.

**Alternatives considered:** Standalone K8s operator (spec 036), sidecar extraction, microservice split. All add deployment complexity without proportional benefit at current scale.

**Reassess when:** Orchestration load exceeds what a single leader replica can sustain, or when the CRD-based dispatch model (spec 036 Phase 3) is implemented.

---

### Agent provider abstraction supports N backends as first-class citizens

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Both Kiro (primary) and OpenCode are first-class agent backends. The `IAgentProvider` abstraction exists to support any number of backends. Kiro was the initial implementation and remains the primary development focus, but OpenCode is maintained as a full peer, not "best effort."

**Context:** The system has 6 agent Docker images (2 backends × 3 stacks). The `AgentProviderFactory` routes to either `KiroCliAgentProvider` or `OpenCodeAgentProvider`. Provider diversity enables competitive evaluation and model/runtime flexibility.

**Alternatives considered:** Single backend (simpler but locks out provider diversity), Kiro-primary with OpenCode as "proof of extensibility" only.

**Reassess when:** A third backend is added (e.g., Claude Code native), or if maintaining both backends creates disproportionate test burden.

---

### Confidence gate is intentionally fail-closed

**Date:** 2026-07-04
**Category:** architecture

**Decision:** The confidence gate treats unknown assessment values as `not_ready` (fail-closed). Non-empty `blockingIssues` forces `not_ready` regardless of recommendation. False negatives (missed valid work) cost less than false positives (broken PRs that waste reviewer time). The `maxAnalysisRetries=2` buffer handles transient model issues.

**Context:** Comparable systems (Devin, OpenHands, Copilot CCA) default to proceeding optimistically. Our conservative design reflects that this pipeline creates real PRs on real repos, and reviewer time is the bottleneck — wasted reviews cost more than wasted agent compute.

**Alternatives considered:** Fail-open with early quality gate check, configurable conservatism per-project.

**Reassess when:** Agent analysis accuracy improves to the point where false-negative rate becomes a measurable productivity drag (tracked via `agent:needs-refinement` label frequency).

---

### Adversarial review is a default pattern for all durable agent outputs

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Adversarial review (discriminator pattern) is the default for any pipeline step where an agent produces durable output — committed code, created issues, modified knowledge, posted comments. Each adversarial review has a feature toggle (`*ReviewEnabled`). Ephemeral outputs (logs, status messages) don't need it. New features that write to repos, create issues, or modify persistent state should include an adversarial review step with a toggle.

**Context:** Empirical experience shows adversarial reviewers ALWAYS find something to correct, including critical bugs. Multi-agent verification improves accuracy by +39.7pp over single-agent (arXiv:2511.16708). The TriAdReview architecture (arXiv:2606.15074) demonstrates systematic quality improvement through triangular adversarial review. The GAN-inspired pattern (generator + discriminator) eliminates self-attribution bias that occurs when the same context evaluates its own output.

**Alternatives considered:** Selective application (only code review), case-by-case per feature, no review for internal artifacts (brain, harness suggestions).

**Reassess when:** Token costs become prohibitive AND quality improvement from review drops below measurable threshold. Currently, the cost-to-quality ratio strongly favors review.

---

### Partial failure contract: enrichment steps are non-fatal, critical path steps are fatal

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Steps whose output is required by a downstream step on the critical path (clone → branch → codegen → quality gates → PR) are fatal on failure. Steps that produce optional enrichment (brain sync, PR description generation, feedback collection, review posting) are non-fatal — they log a warning and the pipeline continues. New steps default to non-fatal unless they produce artifacts consumed downstream.

**Context:** The pipeline has explicit non-fatal annotations: brain sync failure doesn't kill the run, PR description failure continues, posting failure in review pipeline is non-fatal. Comparable systems (Argo `continueOn`, GitHub Actions `continue-on-error`) offer per-step configuration. Our pattern is simpler: the classification follows from whether the step is on the critical path or is enrichment.

**Alternatives considered:** All failures fatal by default (strict), configurable per-step fatality, retry-first with eventual non-fatality.

**Reassess when:** Non-fatal failures accumulate silently and mask real problems. If a "non-fatal" step's absence causes consistent downstream quality drops, it should be reclassified as fatal.

---

### Token vending: private keys never leave orchestrator (security invariant)

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Private keys (GitHub App PEM, GitLab access tokens) NEVER leave the orchestrator container. Agents only receive short-lived tokens via the `TokenVendingService`. This is a hard security invariant — the motivation is that if an agent goes haywire (prompt injection, hallucination, malicious input), it cannot access secrets that would allow persistent harm. The orchestrator has no AI agent in its container.

**Context:** The `TokenVendingService` generates GitHub installation tokens (1-hour expiry). `ProactiveTokenRefresh` on the agent side requests fresh tokens via SignalR when the current one exceeds 45 minutes. The SignalR dependency for refresh is acceptable because agents already depend on SignalR for lifecycle management. This pattern mirrors GitHub Actions' per-job `GITHUB_TOKEN` injection.

**Alternatives considered:** Direct credential injection via K8s secrets (eliminates SignalR dependency but exposes long-lived keys), projected volumes with short-lived tokens (K8s only), environment-dependent strictness.

**Reassess when:** Never. This is a security boundary, not a convenience trade-off.

---

### Code review always uses isolated sessions (no Shared mode)

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Review agents ALWAYS run in isolated sessions (fresh context, no access to codegen conversation history). The `Shared` mode (legacy) must be removed entirely — see #1042. Self-attribution bias is a proven phenomenon (arXiv:2603.04582): models evaluate their own output as more correct when they can see their own reasoning chain. Cross-context review (arXiv:2603.12123) demonstrates that fresh-session review catches significantly more errors.

**Context:** `Isolated` was already the default; `Shared` existed as a legacy backward-compat option. The decision to remove `Shared` reflects that there is no valid use case for it — it actively harms review quality. Isolated mode also enables parallel execution (multiple review agents running concurrently), which is faster.

**Alternatives considered:** Shared with a different model (reduces self-attribution but loses parallelism), configurable per-issue-complexity, keep Shared as escape hatch.

**Reassess when:** Never for the isolation principle. If a future research paper demonstrates that context-aware review outperforms isolated review with appropriate debiasing techniques, reconsider — but current evidence strongly favors isolation.

---

### HMAC key derivation for agent auth — intentional simplicity

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Agent authentication uses `HMAC-SHA256(master_key, agent_id)` to derive per-agent keys from a single master secret. This eliminates the need to manage individual secrets per agent. Legacy agents without an ID fall back to raw master key comparison. Per-agent revocation is not needed because agents are ephemeral containers — if one is compromised, rotate the master key (K8s Secret update + rolling restart).

**Context:** GitHub Actions uses per-job ephemeral tokens. Kubernetes uses per-SA individual credentials. The HMAC pattern is common in IoT device provisioning (AWS IoT). For our system, agents are cattle not pets — individual identity matters for routing and logging, not for granular access control.

**Alternatives considered:** Per-agent individual secrets (enables granular revocation but multiplies secret management), HMAC with scoped master keys per label group.

**Reassess when:** If the system needs per-agent revocation without rotating the master key (e.g., a compromised agent that must be isolated without restarting others). This would require individual secrets or a revocation list.

---

### Telemetry philosophy: instrument every decision point for full run traceability

**Date:** 2026-07-04
**Category:** architecture

**Decision:** The telemetry goal is full run traceability in Grafana Cloud — every step an agent took can be retraced via logs and traces. If a step takes non-trivial time or makes a branching decision, it gets a span. If a countable event matters for operational health, it gets a counter. The purpose is debugging ("the system did A, but I want B — use Grafana to investigate"), not just alerting.

**Context:** Two OTel meters (`CodingAgent.Pipeline`, `CodingAgent.WorkDistribution`), one ActivitySource with spans for every pipeline step, every code review iteration, individual review agents, quality gate sub-steps, all consolidation phases, token vending, drain cycles, hub operations. Custom histogram buckets are tuned for agent run durations (30s to 6h). This depth is closer to production infrastructure services than typical agent platforms.

**Alternatives considered:** Minimal instrumentation (only job-level counters), selective instrumentation (only critical path). Both were rejected because they make post-hoc debugging of agent behavior impossible.

**Reassess when:** OTLP storage costs become disproportionate to debugging value. Mitigation: reduce trace sampling rate rather than removing spans.

### Dual JSON options: strict write (Default), lenient read (Lenient)

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `PipelineJsonOptions.Default` (camelCase, indented, string enums) is the canonical format for all orchestrator-controlled persistence. `PipelineJsonOptions.Lenient` (case-insensitive) is used for deserializing agent-produced JSON, because LLM output is not guaranteed to match strict casing or formatting. The asymmetry is intentional: the system is authoritative on what it writes, but lenient on what it reads from untrusted sources (agent output files, user-edited configs). New deserialization of agent-produced content MUST use `Lenient`. New serialization of orchestrator state MUST use `Default`.

**Context:** SWE-agent and OpenHands use YAML with no strict read/write split. Most systems have a single serialization path. This system's dual-path reflects the reality that LLMs produce unpredictable JSON formatting — lenient parsing prevents data loss from minor casing differences.

**Alternatives considered:** Single option for both (simpler but forces agents to produce exact-format JSON — unreliable with LLMs), strict parsing with explicit error messages guiding agents to correct format.

**Reassess when:** If a standardized agent output schema with enforced formatting becomes viable (e.g., structured outputs with guaranteed casing), the lenient path could be tightened.

---

### Enum serialization: self-annotation is flexible, agent parsability is mandatory

**Date:** 2026-07-04
**Category:** architecture

**Decision:** No strong preference on whether enums self-annotate with `[JsonConverter(typeof(JsonStringEnumConverter))]` or rely on `PipelineJsonOptions.Default` providing the converter globally. The hard requirement is: any enum value that appears in LLM-produced JSON MUST parse correctly regardless of approach. Current practice: 4 enums self-annotate (defense-in-depth for agent-boundary/satellite-assembly usage), 12 rely on global options. Either pattern is acceptable — agents may choose whichever is simpler for the context.

**Context:** .NET guidance recommends global options for consistency. Self-annotation is recommended for library types consumed by external callers. This project's mixed approach evolved organically and both patterns work.

**Alternatives considered:** Mandate all enums self-annotate (belt-and-suspenders), mandate global-only (consistent but fragile).

**Reassess when:** If a deserialization bug is traced to a missing converter (enum parsed as integer), tighten the rule toward mandatory self-annotation for agent-facing enums.

---

### MessagePack int ordinals for SignalR — homogeneous deployment assumed

**Date:** 2026-07-04
**Category:** architecture

**Decision:** SignalR hub communication uses MessagePack with integer ordinal enum serialization. This means enum member ordering is an implicit wire contract. This is acceptable because deployment is homogeneous — orchestrator and agents are always deployed together from the same build. No multi-version scenario exists. Enum members in hub-transmitted types should not be reordered, but no explicit compile-time enforcement exists beyond the `HubMessageSerializationTests` test class.

**Context:** gRPC and Protobuf use explicit numbering to avoid ordinal coupling. Kubernetes handles multi-version compat. This system's simpler approach reflects its deployment model (single Docker Compose or Helm release upgrades all components simultaneously).

**Alternatives considered:** String-based MessagePack enum serialization (safer for multi-version, increases payload size), explicit ordinal value annotations.

**Reassess when:** If the system ever supports rolling upgrades where orchestrator and agents run different versions simultaneously. Currently not planned.

---

### Snake_case `JsonStringEnumMemberName` for LLM-produced enum values

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Enums that appear in agent-produced JSON use `[JsonStringEnumMemberName("snake_case")]` (e.g., `AnalysisGateResult.NotReady` → `"not_ready"`, `CriterionStatus.NonCompliant` → `"non_compliant"`). Enums in orchestrator-controlled JSON use PascalCase (the default). The boundary is: "does an LLM produce this value?" Combined with lenient parsing (Q1), this provides belt-and-suspenders for agent output: explicit snake_case mapping PLUS lenient case-insensitive fallback.

**Context:** OpenAI and Anthropic APIs universally use snake_case for enum values. LLMs are more reliable at producing snake_case than PascalCase. The attribute maps between LLM-natural format and C#-natural naming.

**Alternatives considered:** PascalCase everywhere (consistent but increases LLM parsing failures), snake_case everywhere (alienates .NET conventions).

**Reassess when:** If structured output guarantees from model providers make casing enforcement reliable, the snake_case attributes become less critical (but harmless to keep).

---

### Circuit breaker is an infrastructure safeguard, not a provider health check

**Date:** 2026-07-04
**Category:** architecture

**Decision:** The global circuit breaker (`CheckCircuitBreakerAsync`) is an infrastructure-level safeguard that trips ONLY when ALL enabled templates are failing simultaneously (e.g., network outage, DNS failure, shared API gateway down). It is NOT a per-provider health check. Individual provider failures (e.g., GitHub API down) don't trip it — the loop simply skips that provider's items and continues with others. Per-template failure tracking (`ConsecutiveFailures`) handles rate-limiting and back-off for individual providers. The two mechanisms are complementary tiers: rate-limiting (fine-grained, per-template) and circuit breaker (coarse-grained, system-wide).

**Context:** Argo Workflows has no circuit breaker. Tekton pipelines fail independently. The circuit breaker with auto-resume after 5-minute cooldown reflects the expectation of transient infrastructure outages that self-heal.

**Alternatives considered:** Per-template circuit breaking (isolates failures but adds complexity for a scenario that rarely occurs in practice), no circuit breaker (rely on rate-limiting alone).

**Reassess when:** If the system serves multiple teams with independent infrastructure, per-template circuit breaking may be needed to prevent one team's broken provider from affecting global throughput.

---

### Agent lifetime: pull-model (docker-compose) was initial design, push-model (K8s) is the future

**Date:** 2026-07-04
**Category:** architecture

**Decision:** Two distinct agent lifetime models exist by deployment mode. Docker Compose: agents are persistent containers using a pull-model (connect via SignalR, receive jobs, execute, return to idle). K8s mode: agents are ephemeral pods using a push-model (one K8s Job per WorkItem, container destroyed after completion). The pull-model was the original design. The K8s push-model is the production-scale future. Docker-compose mode may eventually be deprecated once K8s-mode proves itself. `IWorkDistributor` abstracts the difference from the pipeline layer.

**Context:** GitHub Actions uses ephemeral runners. Argo uses ephemeral pods. The pull-model works well for developer/small-team deployments (low-latency, session affinity for resume). K8s ephemeral is better for production (clean-slate isolation, autoscaling, no stale state). The PVC pool in K8s manages credential persistence across ephemeral pods.

**Alternatives considered:** Single model (always ephemeral — locks out non-K8s users), converge immediately (premature — K8s mode is still maturing).

**Reassess when:** K8s-mode dispatch + reconciliation is production-proven. At that point, deprecating docker-compose mode becomes viable.

---

### Cleanup step before PR is intentional quality polish

**Date:** 2026-07-04
**Category:** architecture

**Decision:** After quality gates pass, a dedicated "cleanup" agent call runs before PR creation. This handles cosmetic/style issues the agent introduced during fix iterations (debug artifacts, verbose logging, temporary files). "Tests pass" is necessary but not sufficient for PR quality. The cleanup gets its own QG run afterward. If cleanup introduces failures, the SHARED retry budget handles them (no separate MaxCleanupRetries). This is acceptable because cleanup rarely introduces new failures.

**Context:** No comparable system (Devin, OpenHands, Copilot CCA) has a cleanup step — they go directly from "tests pass" to PR creation. This is novel and reflects the observation that agents leave non-functional debris that reviewers shouldn't need to catch.

**Alternatives considered:** No cleanup (ship as soon as tests pass), advisory-only cleanup (no re-validation), separate retry budget for cleanup-induced failures.

**Reassess when:** If cleanup consistently passes without changes (agents stop leaving debris), the step can be skipped for efficiency. Track via telemetry: cleanup QG pass rate.

---

### Label swap: add-first ordering for crash safety

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `AgentLabelOperations.SwapAsync` adds the target label FIRST, then removes all other agent labels. This ordering is intentional crash-safety: an issue should never be left without a status label (invisible to operators). A transient window where TWO labels coexist is acceptable — dedup detection prevents behavioral bugs, and a human can manually derive the correct state from two labels. GitHub doesn't provide atomic label transactions, so this is the safest available approach.

**Context:** Strict "remove-then-add" would create a window where the issue has NO agent label — invisible and confusing. True atomicity is impossible with GitHub's API. The dedup check (`IsIssueBeingProcessed`) prevents double-dispatch from transient two-label states.

**Alternatives considered:** Remove-first-then-add (label gap risk), batch API call (GitHub doesn't support), external lock (overkill for label operations).

**Reassess when:** If GitHub adds atomic label operations (unlikely), or if the two-label transient state causes operational confusion in practice.

---

### External CI re-push: workaround for GitHub Actions webhook unreliability

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `PollCiWithNotStartedRetryAsync` creates empty commits and force-pushes to re-trigger CI when GitHub Actions fails to trigger workflow runs. This is intentional — GitHub Actions has been increasingly unreliable with webhook delivery. Empty commits are used because they're provider-agnostic (all CI tools trigger on push events). Waiting longer doesn't help because dropped webhooks never self-recover. The force-push on agent-created feature branches is acceptable.

**Context:** No comparable system has automated re-push. Most just fail and require manual retry. The race-avoidance check before re-push prevents unnecessary empty commits when CI starts just before the retry fires.

**Alternatives considered:** Longer timeouts (doesn't help for dropped webhooks), workflow_dispatch API (GitHub-specific, not provider-agnostic), manual retry only.

**Reassess when:** If GitHub fixes webhook reliability, or if the system moves to a CI provider with guaranteed event delivery. The empty commit noise in git history is acceptable for agent branches.

---

### Filesystem-as-context: workspace files are the context delivery mechanism

**Date:** 2026-07-04
**Category:** architecture

**Decision:** The pipeline injects context into agents via workspace files (`.agent/`, `.brain/`, `.kiro/steering/`), NOT via prompt messages. The agent discovers and reads these files through its normal file-reading capability. Prompts tell the agent WHAT to do; files tell it WHAT the context is. This is intentional for three reasons: (1) prompts got too large when context was inline, (2) filesystem avoids escaping issues that plagued large JSON/markdown blocks in prompts, (3) files provide an injection point for security scanning (detect prompt injection in written files before agent reads them).

**Context:** Most agent systems inject context into the prompt window (Claude Code injects CLAUDE.md, Cursor injects rules into system prompt). The filesystem approach is closer to how human developers work (read files in the repo). Files are persistent, inspectable by humans, survive session crashes, and have no size limit.

**Alternatives considered:** Prompt injection (guaranteed in context window but limited by tokens and escaping issues), hybrid (critical context in prompt, supplementary in files).

**Reassess when:** If agents consistently fail to read referenced files (would indicate the prompt needs stronger "read these files first" instructions). Currently working well.

---

### Agent label routing: layered resolution from provider to global default

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `LabelResolver.ResolveRequiredLabels` determines which agent runs a job. The intended resolution is: `ProviderConfig.RequiredLabels` on the repository provider → `PipelineConfiguration.DefaultRequiredAgentLabels` global fallback → empty (any agent). This allows different repos to target different agent stacks (dotnet repo → `kiro,dotnet` agent, python repo → `kiro,python` agent) without requiring per-repo config when a global default suffices. Note: there's a legacy `Settings["requiredAgentLabels"]` path in the code that may be dead code — needs verification.

**Context:** GitHub Actions uses `runs-on` labels for runner selection. Kubernetes uses node selectors. The layered approach handles shared infrastructure where most repos use the default but specific repos need specialized agents.

**Alternatives considered:** Single-level (template-only selector), per-issue label routing (too granular).

**Reassess when:** If the legacy Settings dictionary path is confirmed dead code, remove it to simplify the resolution chain. Issue #1048 tracks removal.

---

### LocalPipelineExecutor: accidental monolith, good refactoring candidate

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `LocalPipelineExecutor` at ~860 lines is NOT intentionally monolithic — it grew over time through feature additions. It's a good candidate for incremental extraction (context records → helpers → step methods). Issues #975, #957, #958 propose valid decompositions. The core orchestration flow (provider construction → context building → step execution → progress reporting) should stay in one file for readability, but ancillary logic (specific step implementations, record types, utility methods) should be extracted when touched.

**Context:** The file has 96 changes in 90 days (#1 hotspot). It acts as the hub-to-pipeline bridge — a deliberate coordination point in concept, but its size is incidental. Comparable "orchestrator" classes in pipeline architectures are typically 300-500 lines.

**Alternatives considered:** Keep as-is (reduces navigation across files), full decomposition into partial classes (fragments the narrative), split into separate step executor classes (too many files for coordination logic).

**Reassess when:** After #975 is implemented (extract records), reassess if further decomposition is needed. Target: core file under 600 lines.

---

### Label lifecycle needs formalization — currently informal state machine (#1046)

**Date:** 2026-07-04
**Category:** architecture

**Decision:** The label transition graph (agent:next → in-progress → done/error/cancelled/needs-refinement/wont-do) is currently enforced implicitly by code structure, which is NOT intentional — it grew organically and has produced bugs in the past. A formal `LabelStateMachine` with explicit transition validation is needed. Issue #1046 tracks the implementation. Valid transitions should be defined in one authoritative location, with runtime validation (warn, don't block) catching invalid transitions.

**Context:** Kubernetes uses strict status conditions validated at the API level. The current system relies on developers knowing the valid transitions — this has caused bugs. The state machine is simple enough to formalize without excessive complexity.

**Alternatives considered:** Keep informal (continues producing bugs), strict blocking validation (too risky for production — might block legitimate edge cases during initial rollout).

**Reassess when:** After #1046 is implemented, evaluate whether blocking mode (throw on invalid transition) is safe enough based on false-positive rate.

---

### Template-level overrides: minimal because no need yet, not intentional constraint

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `ApplyTemplateOverrides` currently only applies `BrainReadOnly` from the template and `BlacklistedPaths` from the repo provider. This is NOT an intentional design constraint — it's simply that no other template-level overrides have been needed yet. The priority chain is: `Global → Project (deep-merge) → Template (currently BrainReadOnly + Blacklist only)`. Template expansion is a future possibility when heterogeneous workloads demand per-template retry/timeout tuning.

**Context:** Templates define routing (which providers, which agent labels). Projects define behavioral configuration. This separation works today. If different templates need different retry counts (e.g., dotnet templates need more retries than python), template-level config expansion would be the path.

**Alternatives considered:** Full template-level overrides now (premature), template inherits all project-level override capabilities (adds maintenance burden without demand).

**Reassess when:** Heterogeneous templates within the same project need different behavioral configuration (e.g., timeout differences between language stacks).

---

### SignalR reconnection: infinite retry for docker-compose, K8s would rely on self-healing

**Date:** 2026-07-04
**Category:** architecture

**Decision:** `InfiniteRetryPolicy` (exponential backoff 1s → 120s cap + jitter) ensures agents never self-terminate from disconnection. This is intentional for docker-compose mode: orchestrator restarts are common during development, and agents should recover automatically. For a future K8s-only setup, self-termination after prolonged disconnection (letting K8s liveness probes → pod restart) would be more appropriate. Currently, both modes use infinite retry.

**Context:** Kubernetes controllers use infinite watch re-establishment. GitHub Actions runners self-terminate after prolonged disconnection (5 min). The 120s cap prevents CPU waste while maintaining ~30s average reconnection latency after orchestrator returns.

**Alternatives considered:** Self-termination after N minutes (appropriate for K8s, harmful for docker-compose), configurable per deployment mode (adds complexity for a non-issue today).

**Reassess when:** K8s-only mode becomes the default. At that point, add a configurable `MaxReconnectionDuration` that defaults to infinite for docker-compose and 5 minutes for K8s (let liveness probes handle recovery).

---

## Configuration

<!-- Decisions about defaults, limits, thresholds, and tunables -->

### Project overrides: intended semantics is deep-merge (currently broken — #1044)

**Date:** 2026-07-04
**Category:** configuration

**Decision:** Project-level overrides for nested config objects (e.g., `CodeReview`) should use deep-merge semantics: only explicitly-set sub-properties override the global config, unspecified sub-properties retain their global values. The current implementation uses REPLACE semantics (entire object swapped), which is a bug. Issue #1044 tracks the fix. Scalar properties already use correct merge semantics via the nullable pattern.

**Context:** Kubernetes uses strategic merge patch. Helm uses deep merge. The current REPLACE behavior means changing one CodeReview property silently resets all others — unintuitive and error-prone.

**Alternatives considered:** Keep REPLACE (simpler implementation but bad UX), configurable per-object (overcomplicated).

**Reassess when:** After #1044 is implemented and template-level overrides are added (should follow the same deep-merge pattern).

---

### Consolidation scheduling: manual-only for now, automated scheduling is future roadmap

**Date:** 2026-07-04
**Category:** configuration

**Decision:** Brain consolidation and refactoring detection are triggered exclusively via UI buttons on the Consolidation page. No automated scheduling exists (no timer, no cron, no event-based triggers). This is sufficient for now. Future evolution: trigger consolidation automatically after every N implementation runs, configurable per-template. This avoids consuming agent budget during peak hours while still keeping the brain clean.

**Context:** SonarQube runs quality scans on schedule. GitHub Dependabot uses cron. Most maintenance tools have automated scheduling. Manual-only is a WIP state, not a permanent design choice.

**Alternatives considered:** Timer-based (every 24h), event-based (after each successful run), hybrid (manual + optional schedule per template).

**Reassess when:** Brain staleness becomes a measurable quality issue, or when users report forgetting to run consolidation. The "every N runs" approach would be the first automation step.

---

### Enum roundtrip test is a mandatory invariant for new pipeline enums

**Date:** 2026-07-04
**Category:** configuration

**Decision:** `PipelineEnumJsonRoundtripTests.cs` is a mandatory test file — any new enum added to the `CodingAgentWebUI.Pipeline` namespace MUST have a corresponding test method and `MemberData` source in this file. The test exhaustively verifies every value of every pipeline enum survives JSON roundtrip as a string (not numeric). This catches silent config corruption at CI time: if an enum value serializes as `0` instead of `"Ready"`, persisted run files become unreadable after code changes.

**Context:** Most projects rely on integration tests to catch serialization bugs. This explicit per-value test is comparable to financial systems and protocol implementations where data corruption is high-severity. The cost is one line per new enum; the benefit is preventing severity-1 runtime failures.

**Alternatives considered:** Rely solely on property tests (covers structural changes but doesn't guarantee individual enum values), rely on integration tests (catches bugs too late in the pipeline).

**Reassess when:** If a compile-time source generator can automatically verify all enums have string serialization configured, the manual test becomes redundant.

---

### No schema versioning — append-only config evolution via nullable properties

**Date:** 2026-07-04
**Category:** configuration

**Decision:** `PipelineConfiguration` has no `version` field. Schema evolution is handled implicitly: new properties are added with default values, old configs missing those properties deserialize fine via `Lenient` options (which don't enforce `required`). Breaking changes (renames, type changes, field removals) are avoided by design. If a field must change meaning, add a new field and deprecate the old one. The `ConfigMigrationService` handles one-time data migrations (e.g., moving from file-based to DB-based storage) but not schema version gating.

**Context:** Kubernetes uses `apiVersion`, Terraform uses schema versions. Simpler tools (Claude Code, Cursor) have no versioning. This system is still WIP — fixing a schema version is premature. The implicit approach works because configs are always written and read by the same codebase version (same homogeneous deployment assumption as MessagePack).

**Alternatives considered:** Explicit `version` field with migration logic per version bump, JSON Schema validation at startup.

**Reassess when:** The system reaches a stable 1.0 where backward compatibility with older configs becomes a user-facing concern, or when a breaking schema change is unavoidable.

---

### MaxRetries=3 is an arbitrary but well-performing default

**Date:** 2026-07-04
**Category:** configuration

**Decision:** `RetryConfiguration.MaxRetries=3` (4 total attempts) and `MaxAnalysisRetries=2` (3 total attempts) are not empirically calibrated — they're "reasonable defaults" that happen to work well subjectively. No formal tuning has been done. The values stay unless evidence shows they're wrong. Per-project override is available via `PipelineProject.MaxRetries` for teams that need different budgets.

**Context:** Devin uses 5 retries. OpenHands uses 3. GitHub Copilot CCA uses 2. Most agent systems converge on 2-4 because each retry costs tokens and diminishing returns set in quickly (same errors tend to repeat).

**Alternatives considered:** Formal A/B testing of retry counts (overkill at current scale), dynamic retry based on error type (adds complexity).

**Reassess when:** If the `agent:error` rate (draft PRs from exhausted retries) becomes a measurable productivity concern, tune empirically. Or if token costs become a concern, reduce to 2.

---

### Dispatch fairness: equal round-robin is sufficient, not intentional design

**Date:** 2026-07-04
**Category:** configuration

**Decision:** `DispatchFairRoundRobinAsync` uses strict three-way interleaving (issues → PRs → decomposition) with no weighting. This is NOT a deliberate fairness model — it's the simplest approach that works at current scale. No work type is prioritized over another. `MaxConcurrentDecompositions` is the only differentiation (caps decomposition parallelism). The system doesn't need weighted scheduling yet.

**Context:** Argo Workflows uses priority classes. Kubernetes uses ResourceQuotas. Most agent orchestrators at this maturity use FIFO or simple round-robin. Weighted fair queuing adds complexity for a problem that doesn't exist yet.

**Alternatives considered:** Weighted scheduling (3:1:1 issue:PR:decomp), configurable per-project priorities, deadline-aware scheduling.

**Reassess when:** Multiple teams share infrastructure and implementation work is visibly starved by review/decomposition volume. Or when the system serves >10 concurrent projects with different priority needs.

---

### MaxRunsPerCycle=0 (unlimited) is intentional — other mechanisms bound concurrency

**Date:** 2026-07-04
**Category:** configuration

**Decision:** `ClosedLoopMaxRunsPerCycle=0` means unlimited dispatch per cycle. This is safe because concurrency is bounded by: agent count (docker-compose), `MaxConcurrentPods` per-selector (K8s), rate limiter (10 Jobs/s in DispatchService), and `MaxConcurrentDecompositions`. The `0=unlimited` default avoids artificial throttling for the common case. Users who need a cap set it explicitly.

**Context:** GitHub Actions has 20-256 concurrent jobs per org. Argo defaults to 500 concurrent workflows. This system's layered concurrency controls make a global per-cycle cap redundant for most deployments.

**Alternatives considered:** Positive default (safety net for misconfigured K8s), dynamic cap based on cluster capacity.

**Reassess when:** If unbounded dispatch causes issues in K8s mode (e.g., pod scheduling pressure), add a positive default for K8s deployments specifically.

---

### Draft PR is the retry-exhausted fallback

**Date:** 2026-07-04
**Category:** configuration

**Decision:** When quality gates fail and all retries are exhausted, the pipeline creates a draft PR with the failing code (labeled `agent:error`) rather than just marking the run as Failed. This preserves the agent's partial work for human inspection and potential manual completion.

**Context:** Most CI/CD systems simply fail. Devin and OpenHands fail outright. The draft PR approach gives humans visibility into what the agent attempted without requiring log diving. Draft PRs are clearly marked and carry the `agent:error` label.

**Alternatives considered:** Fail without PR (clean but loses visibility), configurable per-project, summary-comment-only without PR.

**Reassess when:** Draft PR accumulation becomes a measurable housekeeping burden across repositories.

---

### Refactoring proposal quality bar: hotspot + scope + evidence

**Date:** 2026-07-04
**Category:** configuration

**Decision:** A refactoring proposal is "good" if: (1) it touches a hotspot file (evidence of active development via git log within `HotspotAnalysisLookback`), (2) the scope is achievable by a single agent in one run (<30 files), and (3) the evidence is concrete (specific file paths, specific pattern instances, not abstract advice). The adversarial review enforces these criteria. The outcome feedback loop (tracking `agent:done` vs `agent:wont-do`/`agent:cancelled` on past proposals within `RefactoringOutcomeLookback`) should drive the threshold over time — if >50% of proposals get wont-do'd, the system is too aggressive.

**Context:** The 3-agent Phase 1 pipeline (structural debt, correctness/hygiene, design consistency) produces candidates filtered by Phase 0 conventions and Phase 2 aggregation. The "worth creating an issue" bar must be high enough to avoid noise but low enough to catch real debt. No comparable system does proactive refactoring detection — this is novel territory.

**Alternatives considered:** Hard metric thresholds (SonarQube-style complexity/duplication), pure scope-based filtering (any real issue regardless of hotspot), defer entirely to human calibration via feedback loop.

**Reassess when:** The `agent:wont-do` rate on refactoring proposals exceeds 50% over a 90-day window, indicating the system is too aggressive.

## Future Direction

<!-- Decisions about what IS and IS NOT planned, scope boundaries -->

### Three deployment modes: Legacy → DB+SignalR → DB+Kubernetes (progressive)

**Date:** 2026-07-04
**Category:** future-direction

**Decision:** The system supports three deployment modes representing progressive infrastructure investment. Legacy (in-memory JSON files, zero dependencies) was the initial implementation. DB+SignalR adds Postgres persistence for multi-replica safety. DB+Kubernetes adds K8s Job-based dispatch for production scale. For non-K8s deployments, DB+SignalR is the production path. K8s-only is a possible long-term direction but that decision hasn't been made yet. Legacy mode remains for zero-friction onboarding but is not guaranteed feature parity with DB modes — new persistence-dependent features can be DB-only.

**Context:** The `IWorkDistributor` and `IConfigurationStore` abstractions enable all three modes. Docker Compose is the development/small-team target. Helm chart is the K8s production target. Both deployment targets are first-class. New features requiring work item lifecycle or reconciliation can be DB-only.

**Alternatives considered:** K8s-only (locks out non-K8s users), single mode (loses progressive adoption), deprecate Legacy immediately.

**Reassess when:** The decision to go K8s-only becomes clear (likely after CRD-based dispatch proves itself in production), or if Legacy mode maintenance becomes a test burden without active users.

---

### Dispatch priority is FIFO — no priority queue yet

**Date:** 2026-07-04
**Category:** future-direction

**Decision:** All `agent:next` issues are treated equally in dispatch order (API pagination order, typically oldest-first). There is no priority mechanism. Manual dispatch handles urgent cases. Round-robin budget sharing across work types (implementation, review, decomposition) is the only scheduling intelligence.

**Context:** Tekton and Argo support priority classes; most agent orchestrators at this maturity use FIFO. The system isn't at a scale where priority scheduling justifies the added complexity (starvation prevention, priority inversion, configuration UX). Priority is something that could go on the roadmap but has no mechanism today.

**Alternatives considered:** Label-based priority (e.g., `priority:high` bumps to front), deadline-aware scheduling, weighted fair queuing.

**Reassess when:** Multiple teams share the same pipeline infrastructure and low-value batch work visibly blocks urgent fixes.

## Integration

<!-- Decisions about external systems, APIs, provider boundaries -->

## Scope

<!-- Decisions about what's intentionally excluded or limited -->

### Brain repository uses active consolidation, not append-only

**Date:** 2026-07-04
**Category:** scope

**Decision:** The brain repo is actively curated via a periodic `BrainConsolidation` job — a 4-phase agent workflow that clones the brain, runs consolidation (merge, prune, resolve contradictions), optionally runs adversarial review (`brainConsolidationReviewEnabled`), then commits and pushes. The brain is NOT append-only; it's a living knowledge store with automated maintenance.

**Context:** Research (arXiv:2602.11988) shows naive context accumulation hurts agent performance. The consolidation system addresses this by periodically merging redundant entries, pruning stale knowledge, and resolving contradictions. The adversarial review acts as a quality gate on brain mutations. This is a more sophisticated approach than most comparable systems (Claude Code uses flat CLAUDE.md, Devin uses opaque snapshots).

**Alternatives considered:** Append-only with manual human pruning, periodic full resets per milestone, TTL-based automatic expiry.

**Reassess when:** Consolidation runs consistently produce zero changes (brain is already clean), or when consolidation cost (tokens, latency) exceeds the value of the knowledge maintenance.

---

### Brain ReadOnly mode is for shared/untrusted brain consumers

**Date:** 2026-07-04
**Category:** scope

**Decision:** `BrainReadOnly=true` means the brain is synced pre-run (agent reads knowledge) but NOT written post-run (no reflection, no `.brain/` artifacts committed). Use case: when a template wants brain context but doesn't trust its own runs to contribute quality knowledge — either because it's new, experimental, or a secondary consumer of shared knowledge. The setting should ideally live on the `PipelineJobTemplate` (per-template granularity) rather than only at project/global level — this allows "template A writes to the brain, template B only reads" within the same project.

**Context:** Currently `BrainReadOnly` is on `PipelineConfiguration` (global) with a per-project nullable override. Moving to template-level would give proper granularity for shared brain scenarios. The general pattern is: primary/trusted templates write, secondary/experimental templates read-only.

**Alternatives considered:** Brain access as a provider-level setting (too coarse), per-run override (too granular, no UI for it).

**Reassess when:** Template-level `BrainReadOnly` is implemented. Note: the current project-level override still serves the "all templates in this project are read-only" case.

---

### Steering content: project vs. repo are complementary, not competing

**Date:** 2026-07-04
**Category:** scope

**Decision:** Pipeline steering is delivered as two separate files — `pipeline-project.md` (from project configuration) and `pipeline-repo.md` (from repository provider). They have different concerns: project-level is team/org preferences (code style, tool preferences, behavioral constraints); repo-level is technical specifics (architecture, dependencies, conventions for that specific repo). No conflict resolution mechanism exists because they shouldn't conflict. The agent resolves any ambiguity contextually.

**Context:** For Kiro agents, both files go to `.kiro/steering/` with `inclusion: always` frontmatter. For OpenCode agents, they're concatenated into `AGENTS.md` under "Project Instructions" and "Repository Instructions" headers. The `.kiro/steering/` directory in the workspace is for pipeline-injected content in agent containers — it's separate from the local development `.kiro/steering/` in the source repo.

**Alternatives considered:** Explicit precedence (repo overrides project), merge with priority markers, single combined file.

**Reassess when:** Real conflicts are reported between project and repo steering causing agent confusion. Currently, the separation of concerns prevents this.

---

### Open issue context: cross-issue awareness to prevent conflicting parallel changes

**Date:** 2026-07-04
**Category:** scope

**Decision:** `OpenIssueContextWriter` writes up to `MaxOpenIssuesForContext` (default 50) open issues as markdown files in `.agent/open-issues/`. This gives agents awareness of in-flight work — preventing conflicting changes when multiple agents work in parallel. Issues are rarely isolated; especially for epics, knowing sibling tasks helps agents make coordinated decisions. Currently only OPEN issues are included (via `ListOpenIssuesAsync`). Closed issues are excluded — potentially a gap for recently-completed sibling context.

**Context:** No comparable system (Devin, OpenHands, Copilot CCA) has cross-issue awareness. This is novel and addresses a real multi-agent coordination problem. The 50-issue cap is a reasonable heuristic to prevent context overload.

**Alternatives considered:** No cross-issue context (simpler but leads to conflicting changes), include closed issues (more context but larger I/O and potential noise), per-epic scoping (only sibling issues, not all open).

**Reassess when:** If agents frequently produce conflicting changes despite the context (cap too low?), or if recently-closed issue context proves valuable for continuity. Issue #1049 tracks adding closed sibling issues for epic flows.

---

### Multi-agent code review: 4 specialized reviewers in parallel is intentional (#1047)

**Date:** 2026-07-04
**Category:** scope

**Decision:** The system ships with 4 parallel review agents (Correctness, DotNetSpecialist, SecurityReviewer, TestQualityReviewer), each with a specialized prompt focusing on one concern. This "ensemble review" approach catches issues a single generalist would miss because each agent focuses without distraction. The reviewer configuration is externalized (`ReviewerConfigurationStore`) so users can customize agents, add roles, or disable ones they don't need. Issue #1047 adds a "be thorough" standardized instruction to all reviewer prompts.

**Context:** CodeRabbit and GitHub Copilot CCA use single reviewers. Multi-perspective review with role specialization is uncommon. Research (arXiv:2511.16708) shows multi-agent verification improves accuracy by +39.7pp over single-agent. 4 agents is the default; configurable per use case.

**Alternatives considered:** Single "be thorough" generalist (cheaper but misses domain-specific issues), 2 agents (correctness + security — insufficient for test quality concerns), 6+ agents (diminishing returns, too much consolidation noise).

**Reassess when:** If token costs for 4 parallel review calls become prohibitive, consider reducing to 2-3 with broader role definitions. Track via telemetry: finding-count-per-agent to identify which roles produce the most value.

---

### Feedback loop: outcome tracking infrastructure exists, automated calibration is future

**Date:** 2026-07-04
**Category:** scope

**Decision:** `FeedbackService` collects structured feedback from each run (harness suggestions, issue categorization). `RefactoringOutcomeLookback=90d` tracks whether past refactoring proposals were accepted or rejected. This outcome data is intentional self-calibration infrastructure — the data collection is implemented, but the automated adjustment loop (e.g., "reduce proposal aggressiveness if rejection rate > 50%") is not yet built. Currently the data is available for export and manual inspection only. No concrete feature design exists for automated calibration.

**Context:** SonarQube has "won't fix" tracking. No agent system has automated self-calibration from outcome tracking. The 90-day lookback captures enough history for trend detection. The infrastructure is in place; the intelligence layer is future work.

**Alternatives considered:** No outcome tracking (loses calibration potential), immediate automated adjustment (premature without understanding the feedback patterns), manual-only forever (wastes the collected data).

**Reassess when:** Enough runs accumulate (100+) to detect statistical patterns. At that point, design an automated calibration mechanism (e.g., reduce `MaxRefactoringProposals` when rejection rate exceeds threshold).

## UX

<!-- Decisions about UI/UX choices, user-facing behavior -->

### AgentCoding page is configure + dispatch only — no pipeline progress

**Date:** 2026-07-04
**Category:** ux

**Decision:** The Agent Coding page is exclusively for configuring templates and dispatching work. It does NOT show pipeline progress, output terminals, or run summaries. That was wrongly implemented (likely a leftover from before the remote agent model existed) and is being removed (#1059). The page always shows the same view regardless of whether runs are active — template table, loop controls, manual dispatch. Pipeline observation belongs on Agent Monitoring.

**Context:** `PipelineService.ActiveRun` is set by `PipelineRunLifecycleService` — there is no locally-executing pipeline in the intended architecture. Agents execute pipelines remotely via SignalR/K8s. The inline progress view on Agent Coding was dead code that never triggered correctly in production deployments.

**Alternatives considered:** Keep the progress view for "quick glance" (wrong — monitoring page exists for this purpose).

**Reassess when:** Never. Clear page responsibility boundary.

---

**Date:** 2026-07-04
**Category:** ux

**Decision:** The split between `AgentCoding.razor.cs` and `AgentCodingPageService` grew organically and is NOT a deliberate architectural boundary. The formalized principle going forward (best practice for Blazor Server): **PageService owns all async workflows and persistent state. Component owns render-lifecycle concerns (timers, JS interop, event subscriptions, transient visibility flags like `_showAddForm`).** When touching this file, migrate behavioral state (e.g., `_recentlyToggled`) into PageService. No strong opinion from the human — adopting Blazor best practice as the default. Apply incrementally.

**Context:** PR #1037 extracted drawer/dispatch logic but left several state fields in the component (`_recentlyToggled`, `_showAddForm`, `_showDeleteConfirm`, toast timers). The boundary is fuzzy. Comparable Blazor patterns (MudBlazor, Radzen) typically extract ALL mutable state to services — we accept a middle ground where render-lifecycle stays in the component.

**Alternatives considered:** Full stateless component (all state in service — too many `Func<Task>` callbacks), keep as-is (continues organic growth).

**Reassess when:** If adding a new feature requires modifying both the component AND the service for the same concern — that signals the boundary is wrong.

---

### Undo snackbar: always show for toggles, not loop-conditional

**Date:** 2026-07-04
**Category:** ux

**Decision:** The undo snackbar should appear for ALL toggle operations (template enabled, implementation, review, decomposition) regardless of whether the loop is active. The current loop-conditional behavior and the inconsistency where `_recentlyToggled` is added unconditionally for some toggles but conditionally for others are both unintentional. Fix: make both `_recentlyToggled` and the undo snackbar unconditional for all toggle types. The cost is minimal (5-second snackbar) and it provides consistent UX.

**Context:** The toggle-during-loop restriction was not a deliberate safety design — it grew from the initial implementation where only template-enabled toggling existed. When implementation/review/decomposition toggles were added, they copied the pattern inconsistently.

**Alternatives considered:** Keep loop-only (saves visual noise in idle state but inconsistent), remove undo entirely (loses safety net).

**Reassess when:** If user feedback indicates the snackbar is annoying in idle state, make it configurable or reduce timeout to 3 seconds.

---

### Drawer tabs: current three-component approach is acceptable, open to consolidation

**Date:** 2026-07-04
**Category:** ux

**Decision:** The tabbed drawer approach (three separate components — `IssueDispatchDrawer`, `PrDispatchDrawer`, `EpicDispatchDrawer` — with tab navigation) is intentional but holds no strong commitment. The three-component pattern allows per-type customization (different columns, actions, data models). If a future contributor can achieve the same customization with a single generic `DispatchDrawer<T>` component without sacrificing readability, that's acceptable. The shared CancellationTokenSource across drawers is intentional — opening any drawer cancels pending work from the previous.

**Context:** Implemented in PR #1013 ([UI-09]). The pattern matches GitLab CI's drawer+tabs approach. Adding a new work type (e.g., "Browse Feedback") should create a new component + tab unless the existing components can be generalized cleanly.

**Alternatives considered:** Single generic component (less code but harder to customize per-type), fully independent drawers without tabs (more components, worse UX).

**Reassess when:** A fourth work type is added — at that point, evaluate whether the per-type component pattern scales or whether a generic approach is needed.

---

### Error messages: sticky, no auto-dismiss, must have manual dismiss button

**Date:** 2026-07-04
**Category:** ux

**Decision:** Error messages (`_errorMessage`) MUST be sticky — they never auto-dismiss. They SHOULD have a manual dismiss button (currently missing — known gap). Success messages auto-dismiss after 3 seconds. This asymmetry is intentional: errors represent actionable failures that the user must acknowledge; successes are confirmatory and transient. New code MUST NOT add auto-dismiss timers for error messages. The TODO in the Razor template ("Error messages need a manual dismiss mechanism") is a valid gap to fix.

**Context:** Material Design guidelines and GitHub Actions both use persistent error messages with explicit dismiss. Auto-dismiss for errors is an anti-pattern — users may miss critical failures.

**Alternatives considered:** Auto-dismiss errors after 10-15 seconds (risky — users miss problems), toast-style errors with queue (over-engineered for current complexity).

**Reassess when:** If error accumulation becomes confusing (multiple errors stacking), consider showing only the most recent error with a "previous errors" expandable section.

---

### PipelineService event handling: extract state transitions to PageService, keep JS in component

**Date:** 2026-07-04
**Category:** ux

**Decision:** The `HandleStateChanged` event handler in `AgentCoding.razor.cs` grew organically and mixes testable state-transition logic (`_lastRunId`, `_showCompletionOnly`, `_lastLoopStatus` detection) with untestable UI concerns (JS scroll interop, `StateHasChanged`). The target state: extract state-transition logic into `PageService.HandlePipelineStateChanged(PipelineRun? activeRun)` so it's unit-testable. The component handler becomes a thin wrapper: call PageService, then `StateHasChanged()`, then JS scroll. Issue #1053 tracks this work.

**Context:** Post-extraction (#1037), the component still has non-trivial logic in event handlers that can't be tested without bUnit. The PageService was designed for user-initiated workflows but should also handle system-event state transitions.

**Alternatives considered:** Keep all event handling in component (untestable), move JS interop to service via IJSRuntime injection (weird — services shouldn't own render concerns).

**Reassess when:** After implementing this, if the PageService becomes too large (>1000 lines), consider splitting into sub-services per concern (DrawerService, LoopControlService, EventStateService).

---

### Output lines buffer: lock+snapshot is acceptable, Channel<string> is the future alternative

**Date:** 2026-07-04
**Category:** ux

**Decision:** The `lock (_outputLock)` + `_outputLines.TakeLast(200).ToList()` pattern for streaming agent output works correctly at current output rates (~1-10 lines/second). It is NOT intentional design — it grew pragmatically. The lock is held during render which is theoretically suboptimal but unmeasurable at current throughput. The target migration is `Channel<string>` with a bounded buffer (lock-free write via `TryWrite`, drain-on-render via `TryRead` loop). Issue #1054 tracks this work.

**Context:** Blazor Server docs recommend `InvokeAsync` for cross-thread updates (which is used), but the shared `List<string>` is still manually synchronized. `Channel<T>` is the .NET-native async producer/consumer pattern that eliminates lock contention. The 200-line cap prevents memory growth regardless of approach.

**Alternatives considered:** `ConcurrentQueue<string>` (simpler than Channel but still needs periodic drain), `IObservable<string>` with Rx (over-engineered), keep current (works fine at current scale).

**Reassess when:** Output rates exceed 50 lines/second, or profiling shows render thread contention from the output lock.

---

### Target user: single operator/power-user — no RBAC for now

**Date:** 2026-07-04
**Category:** ux

**Decision:** The primary user is a single operator/power-user who configures, monitors, and manages the pipeline. This is NOT a developer-facing tool where casual users file issues and wait — it's an ops-facing tool for someone who understands the full system. Dispatch to agents is done via labels (external to this UI), so "submitting work" is already separate from "operating the pipeline." Future RBAC (Read/ReadWrite/Admin, similar to ArgoCD) is on the roadmap but not yet needed. UX should optimize for expertise (compact, dense, keyboard-shortcuts-as-bonus) rather than approachability.

**Context:** The navigation flow (Agent Coding → Monitoring → Consolidation → Settings) assumes one person owns the entire pipeline lifecycle. ArgoCD's permission model (Read/ReadWrite/Admin) is the likely future direction. The onboarding checklist handles first-run learning; after that, the UI assumes expertise.

**Alternatives considered:** Developer-facing tool (simpler UI, guided workflows), multi-persona split (admin vs viewer — premature).

**Reassess when:** A second team member needs access, or when the system is deployed as a shared service. At that point, implement ArgoCD-style RBAC with read-only dashboard views.

---

### Visual design: dark-first, light theme exists for accessibility

**Date:** 2026-07-04
**Category:** ux

**Decision:** Dark theme is the primary design. The CSS custom property system (`--bg`, `--surface`, `--accent`, etc.) defines dark as root defaults. Light theme exists via `[data-theme="light"]` override for accessibility/preference but is not the design priority. New UI features should be verified in dark theme; light theme is "should work" not "must look great." The deep purple accent (`#7c3aed`) is designed for dark backgrounds.

**Context:** Developer tools are overwhelmingly dark-first (VS Code, GitHub, terminals). The light theme variables were defined for completeness but haven't received hand-tuning. The theme toggle persists via localStorage.

**Alternatives considered:** Equal polish for both themes (doubles design work), remove light entirely (hurts accessibility for some users).

**Reassess when:** User feedback specifically reports light theme visual issues. Don't proactively invest in light theme polish.

---

### Monitoring refresh: 5-second polling is the correct interval

**Date:** 2026-07-04
**Category:** ux

**Decision:** The Agent Monitoring page should poll at 5-second intervals (not 2s). The current 2-second interval is too aggressive for a single-operator tool — 5 seconds provides adequate freshness without unnecessary server load. The freshness indicator transparency ("Refreshing every 5s") is intentional — it builds trust that data is current. Issue #1058 tracks the change from 2s → 5s.

**Context:** Grafana defaults to 5-second refresh. GitHub Actions uses 5-10s. The 2-second interval was set without deliberation and generates unnecessary load. For a tool where the operator is watching (not automated alerting), 5 seconds is indistinguishable from real-time.

**Alternatives considered:** Event-driven via SignalR (eliminates polling entirely — future possibility), configurable interval (over-engineering for single-operator use), keep 2s (wasteful).

**Reassess when:** If event-driven monitoring is implemented (SignalR push from pipeline events to monitoring page), polling becomes a fallback only.

---

### Interaction model: mouse-first with keyboard as bonus layer

**Date:** 2026-07-04
**Category:** ux

**Decision:** The primary interaction model is mouse-first. Keyboard shortcuts (Esc, ?, arrow keys in drawers) exist as a power-user layer introduced by an agent, not as a core design principle. They provide value for users who want them but are NOT the primary interaction path. Full keyboard-first navigation (tab trapping, roving tabindex, visible focus states in all components) is not a priority. Fix keyboard accessibility bugs when reported; don't proactively invest in keyboard-first features.

**Context:** The `ShortcutHelpOverlay` and global keyboard handler were added by an agent as a UX improvement. The system owner is mouse-primary. WCAG keyboard accessibility is a separate concern from "keyboard-first design" — basic keyboard operability (tab order, focus-visible) should work, but advanced keyboard navigation is optional.

**Alternatives considered:** Keyboard-first (VS Code model — too much investment for current user base), remove keyboard shortcuts entirely (loses the bonus value for power users who find them).

**Reassess when:** Users report relying on keyboard navigation, or accessibility audit requires specific keyboard improvements.

---

### Information density: high for monitoring, open to redesign for other pages

**Date:** 2026-07-04
**Category:** ux

**Decision:** The Agent Monitoring dashboard is intentionally high-density — operators need system state at a glance (Connected/Busy/Idle/Queued + run metrics in a single stats bar). This is correct and should not be reduced. HOWEVER, other pages (Agent Coding, Settings, Consolidation) are open to UX redesign. The Agent Coding page in particular has grown organically and could benefit from better information hierarchy, progressive disclosure, or layout restructuring. Agents proposing UX improvements to non-monitoring pages should feel free to suggest alternatives.

**Context:** Agent Monitoring is an ops dashboard — high density is the genre standard (Grafana, Datadog). Agent Coding is more of a "control panel" — it could benefit from clearer separation of concerns (configuration vs. dispatch vs. active pipeline). The template table, loop controls, and manual dispatch section are all visible simultaneously regardless of what the user is doing.

**Alternatives considered:** Reduce monitoring density (wrong for ops dashboards), apply same density to all pages (wrong — different pages serve different needs).

**Reassess when:** A UX redesign proposal is created for the Agent Coding page. Consider: tabbed sections (Configure | Dispatch | Monitor), collapsible regions, or a "mode" switch that shows only the relevant section based on pipeline state.

---

### Pipeline progress: dual-panel (sidebar + terminal) is intentional

**Date:** 2026-07-04
**Category:** ux

**Decision:** When a pipeline is active, showing BOTH the structured PipelineSidebar (phased steps: Preparation → Analysis → Code Generation) AND the raw OutputTerminal (agent logs) simultaneously is intentional. The sidebar answers "where am I in the pipeline?" — the terminal answers "what's happening right now?" They serve different cognitive needs for an operator watching a run. The pipeline is a structured process (known phases, known steps); the terminal provides raw transparency into the agent's execution. **This view belongs on Agent Monitoring, NOT Agent Coding.** The Agent Coding page wrongly contained an inline progress view triggered by `PipelineService.ActiveRun` — this was never intended and is being removed (#1059). Agent Coding is purely configure + dispatch.

**Context:** No comparable system uses this exact dual-panel approach. GitHub Actions merges structure and output (expandable log groups). Argo uses DAG + per-step logs (similar concept, different layout). The side-by-side layout leverages the operator's screen width — desktop-only assumption is acceptable for this tool.

**Alternatives considered:** Merged view with expandable log groups per step (GitHub Actions style — loses simultaneous visibility), terminal-only with progress bar (loses structured phase awareness), sidebar-only with expandable logs (too cramped).

**Reassess when:** If the terminal output is rarely useful during a run (operators only check post-hoc), consider making it collapsible. Currently, seeing real-time agent output is part of the trust model — the operator knows the agent is actually working.

## Decision Map

### Relationships
- "Dual JSON options (Default/Lenient)" enables "Snake_case JsonStringEnumMemberName for LLM-produced enums"
- "Dual JSON options (Default/Lenient)" enables "No schema versioning — append-only evolution"
- "Enum roundtrip test is mandatory" constrains "Enum serialization: self-annotation is flexible"
- "MessagePack int ordinals for SignalR" scoped by "Monolithic orchestrator is intentional (homogeneous deployment)"
- "No schema versioning" scoped by "Three deployment modes (homogeneous deployment assumption)"
- "Token vending: private keys never leave orchestrator" constrains "MessagePack int ordinals for SignalR" (both assume trusted orchestrator↔agent channel)
- "Circuit breaker is infrastructure safeguard" scoped by "Partial failure contract (enrichment non-fatal, critical path fatal)"
- "Agent lifetime: pull→push evolution" constrains "Three deployment modes" (K8s-only future would collapse to single mode)
- "MaxRunsPerCycle=0 unlimited" scoped by "Agent lifetime dual model" (bounded by agent count in docker-compose, MaxConcurrentPods in K8s)
- "Cleanup step before PR" enables "Confidence gate is fail-closed" (cleanup reduces false negatives from cosmetic issues)
- "MaxRetries=3 arbitrary default" scoped by "Draft PR is the retry-exhausted fallback" (exhausted retries → draft PR, not failure)
- "Dispatch fairness: equal round-robin" scoped by "Dispatch priority is FIFO" (both reflect "sufficient at current scale" philosophy)
- "Label swap: add-first ordering" scoped by "Token vending: private keys never leave orchestrator" (both assume imperfect external APIs)
- "External CI re-push" scoped by "Partial failure contract" (CI is on the critical path — failure is retried, not ignored)
- "Project overrides: deep-merge (#1044)" constrains "No schema versioning" (merge requires distinguishing "not set" from "set to default")
- "LocalPipelineExecutor: accidental monolith" correlates with "Agent lifetime dual model" (executor grew as both modes added features)
- "AgentCoding component ↔ PageService boundary" scoped by "PipelineService event handling" (event state transitions should migrate to PageService per the boundary principle)
- "Undo snackbar: always show" correlates with "Error messages: sticky with dismiss" (both are feedback pattern decisions — success/undo are transient, errors are persistent)
- "Drawer tabs: three-component approach" scoped by "AgentCoding component ↔ PageService boundary" (drawer state lives in PageService, rendering in components)
- "Target user: single operator" scopes "Information density: high for monitoring" (operator expertise justifies dense dashboard)
- "Target user: single operator" scopes "Monitoring refresh: 5-second polling" (single operator means low server load regardless)
- "Visual design: dark-first" scopes "Information density: high for monitoring" (dark theme with purple accent designed for dense data display)
- "Interaction model: mouse-first" constrains "Keyboard shortcuts" (shortcuts are bonus, not primary interaction path)
- "Pipeline progress: dual-panel" scoped by "Target user: single operator" (desktop-assumed, screen-width-leveraging layout)

### Coverage Gaps (auto-detected)
- Coverage is now comprehensive for core architecture, configuration, UX interaction patterns, and visual design
- Remaining undocumented areas: prompt engineering philosophy, acceptance criteria parsing strategy, feedback loop calibration, Agent Coding page layout redesign (flagged as open to suggestions)

### Queued Questions (for next session)
- Agent Coding page layout redesign proposals (user expressed openness to restructuring non-monitoring pages)
- Prompt engineering philosophy (how prompts are structured/maintained)
- Acceptance criteria parsing strategy
- Feedback loop calibration mechanism design
