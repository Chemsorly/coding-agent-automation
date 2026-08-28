# Design Decisions

Human-authored intent behind non-obvious design choices. This file is the authoritative source for "why" questions that can't be inferred from code or genre patterns alone. Generated via the intent-extraction hook.

**Usage:** Agents MUST read this file before proposing changes to understand constraints and deliberate choices. If a decision here contradicts what seems "obvious," the decision wins — the human made it for a reason.

<!-- Intent Extraction Sessions -->
<!-- Session: 12 | Last run: 2026-08-14 | Decisions captured: 64 -->
<!-- Session: 14 | Last run: 2026-08-22 | Updates: T12 audit — marked #2027/#2028/#2025/#1058/#1059 resolved; fixed ConsolidationDispatchHandler→ConsolidationWorkItemDispatchService; T10 decision added; T11 decisions added -->
<!-- Session: 15 | Last run: 2026-08-22 | Updates: T22 — AgentSelectorKey.From() extracted; ScanPagedAsync private helper extracted in PostgresPipelineRunHistoryService; T6/T15/T16/T20 closed; LabelStateMachine.cs stale path fixed -->
<!-- Session: 16 | Last run: 2026-08-28 | Decisions added: 4 (soft anti-affinity, AgentJobTimeoutSeconds backstop, Redis required for multi-replica keepalive, ExternalCiDuration split); issue created: #2133; helm templates fixed (required→preferred anti-affinity, all 4 deployments) -->
<!-- Queued for next session: automated calibration design (when clear mechanism emerges), housekeeping feature calibration (after 50+ runs), AgentCodingPageService razor component decomposition -->

---

<!-- Decisions are grouped by category, alphabetical within each group. -->
<!-- Categories: architecture | scope | configuration | ux | integration | future-direction -->

## Architecture

<!-- Decisions about system structure, patterns, and component boundaries -->

### Agent images rebuild with the control plane — no wire skew window

**Date:** 2026-08-22
**Category:** architecture

**Decision:** Agent images (`kiro-*`, `opencode-*`) are built from the **same git commit** as the control-plane images (orchestrator, api, jobcontroller) in the same CI run (`docker-build` job depends on `build-and-test` in `.github/workflows/ci.yml`). All images share the same `BUILD_COMMIT_SHA`. This means the 27 MessagePack `[Key(n)]` wire types are always in sync between agents and the control plane — no skew window exists. A separate wire-contract snapshot test is therefore unnecessary.

**Verification (2026-08-22):** `.github/workflows/ci.yml` `docker-build` job includes all agent image Dockerfiles (`dockerfiles/kiro/agent-kiro-*.Dockerfile`, `dockerfiles/opencode/agent-opencode-*.Dockerfile`) alongside control-plane Dockerfiles, all with `needs: [build-and-test]`. They are built from `${{ github.sha }}` — the same commit.

**Reassess when:** If agent images are pinned separately (e.g., a `capability` tag that CI does NOT overwrite on every release commit). At that point add a `[Key(n)]` snapshot test as described in the architecture audit.

### AgentHub consolidation cluster extracted into IHubConsolidationOperations (T10)

**Date:** 2026-08-22
**Category:** architecture

**Decision:** `AgentHubDependencies` reduced from 13 to 11 members by extracting the consolidation cluster (`ModelFetchService`, `IConsolidationService`, `ConsolidationBadgeService`) into `IHubConsolidationOperations`. `AgentHub.Consolidation.cs` now delegates to one method on `_consolidationOps` instead of calling three services directly. Registered as singleton; scoped `AgentHubDependencies` still contains `IChangeNotifier` (shared with consolidation).

**Payoff:** `AgentHubConsolidationTests` can now test `HubConsolidationOperations` directly without requiring `null!` for `ModelFetchService` (which was sealed/unmockable).

**Reassess when:** If the remaining 10 deps prove unwieldy, apply the same Facade Service pattern to the IssueOps or Lifecycle clusters.

### Environment variable options binding (T11)

**Date:** 2026-08-22
**Category:** architecture

**Decision:** Created two options classes to replace scattered `Environment.GetEnvironmentVariable` calls:
- `AgentRuntimeOptions` (Agent host): consolidates `AGENT_CHAT_MODE` (×3), `AGENT_LABELS` (×2), `AGENT_CHAT_SESSION_ID`, `AGENT_CHAT_MODEL`, `AGENT_CHAT_EFFORT`, `AGENT_PROVIDER_TYPE`, `OPENCODE_BASE_URL`, `OPENCODE_SERVER_PASSWORD`, `KIRO_CLI_PATH`. Registered via `AgentRuntimeOptionsSetup : IConfigureOptions<AgentRuntimeOptions>`. `AgentConnectionLifecycle` now accepts optional `AgentRuntimeOptions` and uses field-based access.
- `MonolithRuntimeOptions` (Orchestrator host): consolidates `READINESS_DRAIN_DELAY_SECONDS`, `PIPELINE_LOOP_STARTUP_DELAY_SECONDS`, `AGENT_API_KEY`. Bound with `ValidateDataAnnotations().ValidateOnStart()` — misconfigured host fails at startup with a named error. `ReadinessDrainService` registration uses the options value.

Special cases kept as direct env reads (justified): Serilog bootstrap reads (`LOG_LEVEL`, `DB_LOG_LEVEL`), OTel standard vars, K8s Downward API (`POD_NAME`, `POD_NAMESPACE`), EF Core design-time factory.

**Reassess when:** If a new env var is added for config, it must go into the appropriate options class rather than a new `GetEnvironmentVariable` call.

### LoopStatePersistenceService: no leader guard needed — loop gate + 90s delay is sufficient

### PipelineLoopService: full loop must be leader-gated in multi-replica deployments

**Date:** 2026-08-14
**Category:** architecture

**Decision:** In K8s multi-replica deployments, `PipelineLoopService` MUST only run its poll loop when `ILeaderElectionService.IsLeader` is true. This applies to the entire loop — not just the housekeeping sub-step. The rationale: `LoopStatePersistenceService` auto-resumes the loop on pod restart, meaning all replicas would independently start polling if the loop were not leader-gated. Operations that bypass the WorkItem dedup pipeline (most immediately, the housekeeping auto-branch-updater calling `UpdatePullRequestBranchAsync` directly) have no dedup at all and WILL fire concurrently on non-gated replicas. `ILeaderElectionService` is registered unconditionally in `WorkDistributionRegistration.Consolidation.cs` (Kubernetes path); when the gate resolves to null (test environments with no DI registration), the loop runs unconditionally as before.

**Status (2026-08-20):** Implemented. `PipelineLoopService` uses a `_leaderGate` field (`ILeaderGate?`) sourced from `deps.LeaderElection` (= `sp.GetService<ILeaderElectionService>()`). On startup, the loop spins on `_leaderGate is { IsLeader: false }` before entering the activation loop, and links `_leaderGate.LeaderToken` into the run's `CancellationTokenSource` so the loop stops immediately on leadership loss. When `ILeaderElectionService` is not registered (test environments), `_leaderGate` is null and the loop runs unconditionally as before. Issue #1987 is complete.

**Context:** `PipelineLoopService` predates the multi-replica deployment model. `LeaderElectedPollingService` was extracted in #1912 and is used by `DispatchService`, `ConsolidationWorkItemDispatchService`, and `ReconciliationService`. `PipelineLoopService` was not migrated to use `LeaderElectedPollingService` as its base — it has its own `_leaderGate` integration instead.

**Alternatives considered:** Gate only direct API callers like housekeeping (partial gating) — rejected because WorkItem dedup is not a hard guarantee and auto-resume makes full loop gating necessary.

**Reassess when:** Never for the principle. If the activation model changes (e.g., loop is never auto-resumed), revisit whether leader-gating is still needed.

---

### DatabaseMaintenanceService: migrate to LeaderElectedPollingService

**Date:** 2026-08-14
**Category:** architecture

**Decision:** `DatabaseMaintenanceService` should be migrated to extend `LeaderElectedPollingService` (the shared base class used by `DispatchService`, `ConsolidationWorkItemDispatchService`, and `ReconciliationService`). The current ad-hoc pattern — resolving `ILeaderElectionService` once at startup via `IServiceProvider.GetService()` then checking `IsLeader` per cycle — has a documented race: if the service resolves before `ILeaderElectionService` is registered, all subsequent maintenance cycles run without leader gating. The ad-hoc pattern also lacks `LeaderToken` integration, meaning the service does not stop mid-term on leadership loss (it only checks at the next cycle boundary). Migrating removes the race and makes the gating consistent with all other leader-gated services.

**Context:** The TODO comment in `DatabaseMaintenanceService` explicitly documents the DI resolution race. `LeaderElectedPollingService` already supports the simple `PeriodicTimer`-equivalent pattern via `OnPollCycleAsync` + `PollIntervalSeconds`. Maintenance runs hourly — double-execution risk is low but the DI race is a real correctness gap.

**Alternatives considered:** Fix only the DI race (resolve per-cycle) while keeping `PeriodicTimer` structure — partial fix, still no `LeaderToken` integration; inconsistent with the rest of the dispatch infrastructure.

**Reassess when:** Never — once migrated, this decision is stable.

---

### AgentCodingPageService: razor component is the current decomposition target

**Date:** 2026-08-14 · **Updated:** 2026-08-22
**Category:** ux

**Decision:** `AgentCodingPageService` is now 449 lines (down from ~747). The service decomposition was partially completed. However, `AgentCoding.razor.cs` grew to 572 lines (was ~501, target was timer/dismiss-only) — logic landed in the component rather than leaving it. The razor component is now the higher-priority decomposition target: business logic should be extracted back into services on the next touch.

**Context:** Same growth pattern as `LocalPipelineExecutor` (which was also flagged as an incidental monolith and decomposed). The `DrawerStateService<T>` generic was already extracted from the page service. The known `DrawerCancellationToken` bug (#2028) is a symptom of tight coupling between drawer instances and the parent service.

**Alternatives considered:** Keep as-is (coherent vertical slices are readable), service decomposition only (accomplished; next step is the component).

**Reassess when:** After the razor component's business logic is extracted, verify that the per-drawer split is the right granularity or reconsider.

---

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

### Token vending: private keys never leave orchestrator or API containers (security invariant)

**Date:** 2026-07-04 · **Updated:** 2026-08-22 (post-041–045 topology correction)
**Category:** architecture

**Decision:** Private keys (GitHub App PEM, GitLab access tokens) NEVER leave the API or orchestrator containers. Agents only receive short-lived tokens via the `TokenVendingService`. This is a hard security invariant — the motivation is that if an agent goes haywire (prompt injection, hallucination, malicious input), it cannot access secrets that would allow persistent harm. Neither container runs an AI agent.

**Topology (verified 2026-08-22):**
- GitHub App PEM and GitLab access tokens live exclusively in the database (`ProviderConfig.Settings["privateKeyBase64"]` / `Settings["accessToken"]`). No K8s Secret in the Helm chart contains these credentials — the chart manages only `agent-api-key` (the HMAC master key).
- `TokenVendingService` is registered in both `CodingAgentWebUI.Api` and `CodingAgentWebUI.Orchestration`. Both processes read `ProviderConfig` from Postgres and perform the GitHub JWT exchange in-process.
- Agent pods receive only: (a) the HMAC-derived per-job key (`HMAC-SHA256(master, jobName)`, mounted from the chart Secret), and (b) short-lived GitHub installation access tokens (1-hour expiry) vended via SignalR `RefreshToken` calls handled by `AgentTokenRefreshService` in the API hub. The raw PEM and long-lived GitLab PATs never enter the agent container.
- The agent's `HubConnectionManager` receives the master key at construction and derives its per-agent key internally (`DeriveKey(masterKey, agentId.Value)`) — it never sends the master key over the wire.
- Assembly boundary: NetArchTest rules in `LayerBoundaryTests.cs` prevent `CodingAgentWebUI.Agent`, `Agent.KiroCli`, and `Agent.OpenCode` from depending on `CodingAgentWebUI.Orchestration` (where `TokenVendingService` and `ProviderSettingKeys.PrivateKeyBase64` live). This is structural enforcement, not a named invariant test.

**Known limitation — GitLab PATs:** GitLab access tokens are passed through in plaintext (no vending). Unlike GitHub App tokens (short-lived and scoped), GitLab PATs are long-lived. Recommend project access tokens with ≤1-day expiry to minimize exposure if an agent is compromised.

**DerivedKeySecretName footgun:** `JobSpecBuilder.BuildContext.DerivedKeySecretName` must NEVER be set for work-item or consolidation pods. Setting it injects an already-derived key, causing `HubConnectionManager` to double-derive and fail auth. The comment in `DispatchLifecycleService.cs` and `DispatchLoop.cs` is the only guard. If this configuration needs to be made impossible to express accidentally, add a startup validation guard in `JobSpecBuilder`.

**Context:** The `TokenVendingService` generates GitHub installation tokens (1-hour expiry). `AgentTokenRefreshService` handles mid-job refresh requests from agents over SignalR (triggered when token age exceeds 45 minutes). The SignalR dependency for refresh is acceptable because agents already depend on SignalR for lifecycle management. This pattern mirrors GitHub Actions' per-job `GITHUB_TOKEN` injection.

**Alternatives considered:** Direct credential injection via K8s secrets (eliminates SignalR dependency but exposes long-lived keys), projected volumes with short-lived tokens (K8s only), environment-dependent strictness.

**Reassess when:** Never for the isolation principle. If GitLab short-lived project access tokens become available, migrate to those to match GitHub App token lifetime.

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

**Status (2026-08-17):** Resolved in Spec 043. JobSpecBuilder now vends HMAC-SHA256(master, agentId) via per-Job Secret with ownerReference. WorkItemHttpClient appends ?agentId= on GetAssignment and PostStatus. The divergence predated specs 041–045; prior to this spec, every agent pod mounted the master Secret directly despite the HMAC derivation logic existing at the auth layer.

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

**Status (2026-08-16):** Spec 041 removed the docker-compose deployment target and the Legacy/SignalR work distribution modes. Kubernetes Jobs are now the only work distribution mechanism. The pull-model (docker-compose) is gone; the push-model (K8s) is now the only runtime. The `IWorkDistributor` abstraction was retained — `KubernetesWorkDistributor` is the sole implementation.

**Context:** GitHub Actions uses ephemeral runners. Argo uses ephemeral pods. The pull-model works well for developer/small-team deployments (low-latency, session affinity for resume). K8s ephemeral is better for production (clean-slate isolation, autoscaling, no stale state). The PVC pool in K8s manages credential persistence across ephemeral pods.

**Alternatives considered:** Single model (always ephemeral — locks out non-K8s users), converge immediately (premature — K8s mode is still maturing).

**Reassess when:** K8s-mode dispatch + reconciliation is production-proven. At that point, deprecating docker-compose mode becomes viable.

---

### Helm values: `agents` and `jobTemplates` are separate concerns

**Date:** 2026-07-07
**Category:** architecture

**Decision:** The Helm `values.yaml` separates `agents[]` (SignalR mode Deployments) from `jobTemplates[]` (Kubernetes mode Job pod specs). These serve fundamentally different purposes: `agents` creates persistent Deployments with PVCs, health probes, and rolling update strategies; `jobTemplates` defines ephemeral Job pod specs (image, resources, securityContext, initContainers) rendered into a ConfigMap consumed by `DispatchService`. The ConfigMap template falls back to `agents[]` when `jobTemplates` is empty for backward compatibility.

**Status (2026-08-16):** Spec 041 removed `agents[]` from `values.yaml`. `jobTemplates[]` is now the sole definition for agent pod specs. The ConfigMap fallback to `agents[]` is gone. The separation rationale is now historical — the two values sections have been collapsed to one.

**Context:** Originally a single `agents[]` field served both modes. In SignalR mode it creates Deployments; in K8s mode it only produced a ConfigMap (no Deployments). The dual-purpose design caused confusion: K8s-only fields (maxConcurrent, initContainers for permission fixers) mixed with Deployment-only fields (persistence, strategy, affinity). The split clarifies: `agents` for what runs persistently, `jobTemplates` for what ephemeral Job pods look like.

**Alternatives considered:** Keep unified `agents[]` with mode-conditional rendering (confusing for users — fields that do nothing in one mode), rename `agents` to `jobTemplates` globally (breaks existing SignalR deployments).

**Reassess when:** If docker-compose/SignalR mode is deprecated entirely (per "Agent lifetime" decision above), `agents[]` becomes dead config and can be removed — only `jobTemplates[]` would remain.

---

### GitHub mergeability mapping: correctness-driven, conservative null for unknown states

**Date:** 2026-08-14
**Category:** architecture

**Decision:** In `GitHubRepositoryProvider.IsPullRequestBehindBaseAsync`, the `mergeable_state` values `"blocked"` and `"unstable"` map to `null` (CI in-flight, keep concurrency slot), NOT `false` (done). All unrecognized future states also default to `null`. This is a correctness requirement, not a preference: GitHub returns `"blocked"` for the entire CI run duration when required status checks are configured — mapping it to `false` would free the housekeeping concurrency slot immediately after initial mergeability computation (seconds), before CI runs at all, defeating the concurrency gate. The `null` semantic means "outcome unknown, slot remains in-flight until provider state resolves." No strong opinion on the internal representation; the behavior being correct is the only requirement.

**Context:** `"clean"` (CI passed, up-to-date), `"dirty"` (merge conflict), `"draft"`, and `"has_hooks"` map to `false` (slot free). Slot semantics: `true` = update needed, `false` = done/unupdatable, `null` = CI running. Documented in spec 040 design.md decision table.

**Alternatives considered:** Map `"blocked"` → `false` (incorrect — defeats the concurrency gate), map unknowns → `false` (aggressive slot-free, risks race conditions on new GitHub states).

**Reassess when:** GitHub changes `mergeable_state` semantics, or a new state is added that needs explicit classification. Default behavior (new states → `null`) is the safe fallback.

---

### DispatchGatedLabels: extensible set for human-approval-required label transitions

**Date:** 2026-08-14
**Category:** architecture

**Decision:** `AgentLabels.DispatchGatedLabels` is a general-purpose extensibility mechanism — a `HashSet<string>` of labels that agents are not permitted to self-set via `RequestLabelChange`. Currently contains only `EpicApproved` (the human gate between epic Phase 1 analysis and Phase 2 execution). When a new pipeline state requires explicit human approval before proceeding (e.g., a hypothetical `agent:deploy-approved` or `agent:escalate-approved`), that label belongs in this set. The hub logs a Warning and silently ignores any agent attempt to self-set a gated label — the agent cannot escalate its own privileges by setting its own transition label.

**Context:** Implemented as a guard in `AgentHub.Pipeline.cs` (`RequestLabelChange`). The `targetKind` parameter from the caller is also overridden server-side from `run.RunType` to prevent routing manipulation. Currently only `EpicApproved` is gated; future cases may arise as the pipeline gains more multi-step workflows with human checkpoints.

**Alternatives considered:** Per-label config for gating (overcomplicated), webhook-based approval gates (different mechanism — label-based is simpler for GitHub-native workflows).

**Reassess when:** A new human-approval gate is added that does not fit the label model (e.g., requires a UI action or API call rather than a label change). For label-based gates, always add to `DispatchGatedLabels`.

---

### LeaderElectedPollingService: two correctness defects — resolved by #2027

**Date:** 2026-08-14 · **Closed:** 2026-08-22
**Category:** architecture

**Decision:** `LeaderElectedPollingService` had two correctness defects: (1) Missing `ArgumentNullException.ThrowIfNull(leaderElection)` in the constructor. (2) A broken `OperationCanceledException` catch filter that caused a spurious `Error` log on clean pod shutdown. Both are fixed: (1) `ThrowIfNull` added to constructor. (2) The OCE catch now uses `if (stoppingToken.IsCancellationRequested) break` to correctly distinguish host-stop from leadership-loss. #2027 complete.

**Reassess when:** Never — both fixes are stable.

---

### DrawerCancellationToken: wrong-token bug — resolved by #2028

**Date:** 2026-08-14 · **Closed:** 2026-08-22
**Category:** ux

**Decision:** `AgentCodingPageService.DrawerCancellationToken` property has been removed. Callers access per-drawer `CancellationToken` directly from the specific `DrawerStateService<T>` instance (`IssueDrawer.CancellationToken`, `PrDrawer.CancellationToken`, `EpicDrawer.CancellationToken`). #2028 complete.

**Reassess when:** Never — stable once the property is gone.

---

### ProviderConfigId validation: fail-fast ArgumentException at dispatch entry points is acceptable

**Date:** 2026-08-14
**Category:** architecture

**Decision:** No strong preference on whether empty `ProviderConfigId` at a dispatch entry point throws `ArgumentException` or returns a soft `(false, error)`. Both are valid. The current implementation throws — this is consistent with treating an empty provider ID as a programming error (the UI validates provider selection before dispatch). Agents adding new dispatch entry points may use either pattern, but should be consistent with the method's existing error-return contract (`TryDispatch*` methods that already return `(bool, string?)` may prefer soft returns; void or fire-and-forget paths should throw). No rule to enforce here.

**Context:** Added in #1995/#2005 to prevent a regression where empty `RepoProviderConfigId` caused lifecycle-tracking to be silently skipped, allowing duplicate dispatches. The fix is correct regardless of throw vs return.

**Alternatives considered:** N/A — no strong opinion.

**Reassess when:** Never. Implementation detail, not a load-bearing design decision.

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

**Decision:** `LabelResolver.ResolveRequiredLabels` determines which agent runs a job. The resolution is: `ProviderConfig.RequiredLabels` on the repository provider → `PipelineConfiguration.DefaultRequiredAgentLabels` global fallback → empty (any agent). This allows different repos to target different agent stacks (dotnet repo → `kiro,dotnet` agent, python repo → `kiro,python` agent) without requiring per-repo config when a global default suffices.

**Context:** GitHub Actions uses `runs-on` labels for runner selection. Kubernetes uses node selectors. The layered approach handles shared infrastructure where most repos use the default but specific repos need specialized agents.

**Alternatives considered:** Single-level (template-only selector), per-issue label routing (too granular).

**Reassess when:** If a third resolution layer is needed (e.g., per-issue or per-template label overrides).

---

### LocalPipelineExecutor: decomposition complete

**Date:** 2026-07-04 · **Closed:** 2026-08-22
**Category:** architecture

**Decision:** `LocalPipelineExecutor` is now 334 lines — well under the original 600-line target. Decomposition is complete; no further action needed. Issues #975, #957, #958 implemented the extraction of context records, helpers, and step methods.

**Reassess when:** If the file grows past 600 lines again, reapply the same extraction pattern.

---

### Label lifecycle: formal state machine delivered (#1046)

**Date:** 2026-07-04 · **Closed:** 2026-08-22
**Category:** architecture

**Decision:** The label transition graph (agent:next → in-progress → done/error/cancelled/needs-refinement/wont-do) is enforced by `LabelStateMachine` with explicit transition validation. Issue #1046 implemented the formal state machine. Valid transitions are defined in one authoritative location with runtime validation.

**Reassess when:** If blocking mode (throw on invalid transition) was not enabled at implementation, revisit based on false-positive rate from warn mode.

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

**Status (2026-08-16):** Spec 041 removed docker-compose mode. All agents are now ephemeral K8s Jobs. For work-item pods this is largely moot (the Job terminates after one work item). For chat pods (`AgentWorkerService`), infinite retry is retained — chat pods use SignalR throughout their lifetime and should reconnect automatically if the orchestrator restarts. The `InfiniteRetryPolicy` remains in place for chat pods.

**Context:** Kubernetes controllers use infinite watch re-establishment. GitHub Actions runners self-terminate after prolonged disconnection (5 min). The 120s cap prevents CPU waste while maintaining ~30s average reconnection latency after orchestrator returns.

**Alternatives considered:** Self-termination after N minutes (appropriate for K8s, harmful for docker-compose), configurable per deployment mode (adds complexity for a non-issue today).

**Reassess when:** K8s-only mode becomes the default. At that point, add a configurable `MaxReconnectionDuration` that defaults to infinite for docker-compose and 5 minutes for K8s (let liveness probes handle recovery).

---

## Configuration

<!-- Decisions about defaults, limits, thresholds, and tunables -->

### Project overrides: deep-merge semantics implemented (#1044 resolved)

**Date:** 2026-07-04 (updated 2026-07-25)
**Category:** configuration

**Decision:** Project-level overrides for nested config objects use deep-merge semantics via `[ProjectOverridable(DeepMerge = true)]`. Only explicitly-set sub-properties override the global config; unspecified sub-properties retain their global values. `PipelineConfigurationResolver` implements the merge logic at runtime. Scalar properties continue using the existing nullable pattern. **Status: Implemented and verified.** Issue #1044 closed July 9, 2026.

**Context:** Kubernetes uses strategic merge patch. Helm uses deep merge. The implementation follows the nullable property pattern on override records — matching the suggested approach from the original issue.

**Alternatives considered:** Keep REPLACE (simpler implementation but bad UX), configurable per-object (overcomplicated).

**Reassess when:** Template-level overrides are added (should follow the same deep-merge pattern). A drift-detection test guards against new DeepMerge properties being added without verification.

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

### Dispatch priority: static ordering Review > Decomposition > Implementation > Consolidation

**Date:** 2026-08-14 (supersedes 2026-07-04 "equal round-robin" entry)
**Category:** configuration

**Decision:** Dispatch uses a static priority ordering: Review (PRs) first, then Decomposition, then Implementation (Issues), then Consolidation (lowest, dispatched via separate path). Within each tier, FIFO order is preserved. The ordering is a hardcoded `DispatchTurn[]` array in `DispatchScheduler` and a priority-bucket scan in `JobDeduplicationGuardService` — not configurable at runtime. Starvation prevention is explicitly out of scope.

**Context:** Implemented in #1931. The rationale: Review jobs unblock humans actively waiting on feedback (highest latency sensitivity). Decomposition unblocks multiple future Implementation runs — one decomposed epic creates N tasks, so decomposing early has compound throughput value. Implementation is background work; it runs to completion regardless. Consolidation is housekeeping. The previous design (strict three-way round-robin with no weighting) was replaced because a Review job enqueued after 10 Implementation jobs would wait behind all of them — visibly bad UX when a developer is watching for review feedback. Static ordering (no configuration) was chosen over configurable weights; starvation of Implementation by Decomposition is acknowledged as a theoretical risk but not prioritized.

**Alternatives considered:** Configurable priority weights (adds complexity for a problem not yet observed in practice), age-based starvation promotion (deferred), keeping round-robin (replaced because it ignores latency sensitivity by job type).

**Reassess when:** Multiple teams share infrastructure and Implementation work is visibly starved by Decomposition/Review volume, or when a configurable priority weight becomes a concrete request.

---

### Housekeeping auto-update concurrency: 1 is the correct permanent default

**Date:** 2026-08-14
**Category:** configuration

**Decision:** `HousekeepingConcurrencyLimit = 1` (one branch update in-flight per repo provider per poll tick) is not a conservative "start low and tune" default — it is the correct long-term default for the current deployment topology. The housekeeping feature serves as a de-facto merge queue: updates are serial because GitHub's merge queue itself is serial. Having more than one simultaneous branch update in-flight does not improve throughput in this model; the work happens server-side (CI runs), not client-side (API calls). The property is configurable for teams with different CI topologies, but the default reflects that serial is almost always correct.

**Context:** `HousekeepingConcurrencyLimit` is `PipelineConfiguration` Key(72). Added in spec 040 (auto-branch-updater). Per-template override is available.

**Alternatives considered:** Higher default (e.g., 3) for parallel CI topologies — rejected because the current deployment has a single merge queue; concurrent updates would queue behind each other at the CI level anyway, buying nothing.

**Reassess when:** A deployment accumulates multiple repos with independent CI pipelines where parallel branch updates would genuinely reduce wall-clock time to merge.

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

### Refactoring consolidation loop: autonomous up to PR creation, merge is human-gated

**Date:** 2026-07-25
**Category:** architecture

**Decision:** The refactoring consolidation loop (proposal generation → adversarial review → issue creation → agent implementation → code review → PR creation) is fully autonomous. No human approval is needed for any step up to PR creation. Merging PRs remains a manual human action. The loop can generate 30+ issues in a batch and agents immediately pick them up — this is intended behavior. The adversarial review + wont-do tracking + `agent:needs-refinement` label constitute sufficient quality gating before human merge review.

**Context:** The July 23-25 consolidation batch created 30+ issues automatically, all implemented and PR'd without human intervention. Comparable systems (Dependabot, SonarQube) create proposals for human triage before implementation. This system's higher autonomy reflects trust in the multi-agent review pipeline (generator + discriminator pattern) and the fact that merge remains the human checkpoint.

**Alternatives considered:** Human approval at issue creation (delays throughput without proportional safety gain given adversarial review), auto-merge for effort:small issues (removes the human checkpoint entirely — not desired).

**Reassess when:** If merged PRs from the consolidation loop consistently introduce regressions, or if the `agent:needs-refinement` / `agent:wont-do` rate exceeds 30% on refactoring proposals.

---

### Refactoring auto-dispatch with dependency chains: acceptable for simple tasks only

**Date:** 2026-07-25
**Category:** architecture

**Decision:** Agents creating issues with `Depends on #N` / `Blocked by #N` declarations (via DependencyResolver) is acceptable for simple refactoring tasks. The adversarial reviewer validates dependency correctness. For complex multi-step stories (epic-decomposition), a separate human-reviewed workflow handles dependency and ordering — agents do NOT autonomously create complex dependency chains for architectural work.

**Context:** The refactoring loop creates dependency chains for mechanical ordering (extract class X before migrating its consumers). These are safe because each individual issue is small/low-risk. Epic decomposition uses a dedicated workflow with human approval at the decomposition stage.

**Alternatives considered:** All dependency declarations require human approval (would block simple mechanical orderings), TTL-based auto-release (masks real dependency issues rather than surfacing them).

**Reassess when:** A bad dependency declaration blocks legitimate work for >24 hours without human detection, or when the refactoring loop starts creating chains longer than 3 issues deep.

---

### Value types, dispatch decomposition, temporal coupling: implementation details — follow DDD best practices

**Date:** 2026-07-25
**Category:** architecture

**Decision:** No strong preference on implementation-level patterns (value type consistency, dispatch class decomposition boundaries, DI temporal coupling). Agents should follow general DDD best practices and .NET conventions. Specific patterns: value types should validate on construction per DDD; class extraction follows single-responsibility principle; temporal coupling should be minimized via Lazy<T> or constructor injection when practical. These are not decisions that need human arbitration — agents may choose the idiomatic approach.

**Context:** Five value types exist with varying patterns (bidirectional vs unidirectional implicit conversion, presence/absence of null validation). DispatchService was decomposed into 5+ services. DispatchInfrastructure has a mutable setter. All of these are normal .NET/DDD engineering choices that don't require human specification.

**Alternatives considered:** N/A — this is explicitly "no strong opinion, follow best practices."

**Reassess when:** Never — implementation details don't need human arbitration.

---

### Project overrides: deep-merge is implemented (resolves #1044)

**Date:** 2026-07-25
**Category:** configuration

**Decision:** Deep-merge for project-level nested config overrides is implemented and working. `PipelineConfigurationResolver` uses the `[ProjectOverridable(DeepMerge = true)]` attribute flag to invoke `ApplyOverrides` on nested config objects. Currently only `CodeReview` uses DeepMerge; a drift-detection test guards against adding new DeepMerge properties without verification. Issue #1044 was closed July 9, 2026.

**Context:** The previous decision entry ("Project overrides: intended semantics is deep-merge — currently broken — #1044") is now resolved. The implementation uses a nullable property pattern on override records + ApplyOverrides methods, exactly as suggested in the original issue.

**Alternatives considered:** N/A — the bug was fixed as specified.

**Reassess when:** A second `DeepMerge = true` property is added (the drift-detection test will fire, ensuring verification).

---

**Date:** 2026-07-04
**Category:** configuration

**Decision:** A refactoring proposal is "good" if: (1) it touches a hotspot file (evidence of active development via git log within `HotspotAnalysisLookback`), (2) the scope is achievable by a single agent in one run (<30 files), and (3) the evidence is concrete (specific file paths, specific pattern instances, not abstract advice). The adversarial review enforces these criteria. The outcome feedback loop (tracking `agent:done` vs `agent:wont-do`/`agent:cancelled` on past proposals within `RefactoringOutcomeLookback`) should drive the threshold over time — if >50% of proposals get wont-do'd, the system is too aggressive.

**Context:** The 3-agent Phase 1 pipeline (structural debt, correctness/hygiene, design consistency) produces candidates filtered by Phase 0 conventions and Phase 2 aggregation. The "worth creating an issue" bar must be high enough to avoid noise but low enough to catch real debt. No comparable system does proactive refactoring detection — this is novel territory.

**Alternatives considered:** Hard metric thresholds (SonarQube-style complexity/duplication), pure scope-based filtering (any real issue regardless of hotspot), defer entirely to human calibration via feedback loop.

**Reassess when:** The `agent:wont-do` rate on refactoring proposals exceeds 50% over a 90-day window, indicating the system is too aggressive.

---

### Code review iteration: CRITICAL-only triggers re-review, warnings get one fix pass

**Date:** 2026-07-25
**Category:** architecture

**Decision:** The `FixPromptDecision` escalation strategy is intentional: CRITICAL findings → fix + continue iterating (defects require resolution). Warnings/suggestions only → fix once (add TODO comments) then EXIT the review loop (no re-review). Zero findings → exit immediately. Warnings are low-complexity and typically resolved in a single fix iteration. Adding a TODO for a warning is acceptable because the refactoring consolidation loop will eventually find and fix TODOs. Only CRITICALs — actual defects — justify burning another full review iteration (4 agents × token cost).

**Context:** CodeRabbit and GitHub Copilot CCA don't have fix loops. Devin has implicit retry without severity-based escalation. The CRITICAL-only-retry pattern optimizes for throughput: a warning that introduces a new issue during fix won't be caught until human review, but the probability is low for TODO-level changes. `MaxIterations` config bounds total cost regardless.

**Alternatives considered:** Re-review after warning fixes (doubles cost for marginal gain), binary pass/fail with no severity distinction (loses the "defect vs polish" nuance).

**Reassess when:** If warning fixes frequently introduce new bugs (would indicate the fix agent is unsafe for unsupervised changes). Track via: human review comments on WARNING-level fix commits.

---

### Epic decomposition: two-phase with human gate, sub-issue sizing constraint (currently ≤5, increasing to ~12)

**Date:** 2026-07-25
**Category:** architecture

**Decision:** Epic decomposition uses a two-phase workflow with a mandatory human gate between phases. Phase 1: agent explores codebase → produces plan → adversarial review → posts plan as issue comment → swaps to `agent:epic-review`. Phase 2 (after human approves via `agent:epic-approved`): agent generates full sub-issue JSON files. The human gate prevents bad decompositions from creating N issues that all fail. Each sub-issue is sized to: one verification criterion, one agent run, and a configurable file limit. The ≤5 file limit was the initial conservative default but is too restrictive — increasing to ~12 is planned. Auto-approval when adversarial review passes with 0 findings is a future possibility but not currently implemented.

**Context:** No comparable system has a two-phase human-gated decomposition workflow. Devin decomposes internally without checkpoints. The structured sizing constraints ensure each sub-issue is achievable by a single agent run. The consolidation loop proves agents handle 10-15 file mechanical changes routinely.

**Alternatives considered:** Auto-approve on clean review (faster but removes the human quality check for architectural decisions), fully autonomous decomposition (high risk for complex work).

**Reassess when:** Auto-approval is implemented (removes latency when review passes cleanly), or when the file limit needs further tuning based on agent success rates on decomposed sub-issues.

---

### PR creation: draft-first then finalize, hybrid template + agent narrative

**Date:** 2026-07-25
**Category:** architecture

**Decision:** PRs follow a draft-first-then-finalize pattern: `CreateDraftPrIfNotExistsAsync` creates a minimal draft during implementation (for pipeline visibility and structural purposes), then `FinalizePullRequestAsync` adds the full body (test results, coverage, file changes, code review findings, AC compliance table) and marks ready for review. The body is hybrid: deterministic template (`PipelineFormatting.GeneratePrBody`) provides structure/metrics, agent narrative (`BuildPrDescriptionPrompt`) provides "Summary" and "Approach" sections explaining what and why. Template is authoritative (always present, correctly formatted); agent narrative is enrichment (non-fatal if generation fails).

**Context:** Dependabot uses purely template bodies. Devin uses purely LLM-generated descriptions. This hybrid ensures reviewers always get consistent metrics while also getting human-readable explanations. The draft state exists for pipeline visibility — the issue has an associated PR from early in the run, enabling operators to follow progress.

**Alternatives considered:** Fully agent-generated (risks inconsistent structure, no guaranteed test stats), template-only (loses the "why" context that helps reviewers).

**Reassess when:** If reviewers report the agent narrative is unhelpful or misleading — consider dropping it to save tokens. Currently it adds value for understanding implementation strategy.

---

### Brain knowledge: experimental, append-only with git versioning, consolidation handles maintenance

**Date:** 2026-07-25
**Category:** scope

**Decision:** The brain feature is experimental. The lifecycle is: pre-run clone/pull → agent reads knowledge → agent appends lessons learned (structured: general/technology/projects/sessions, source attribution, citation tracking) → post-run detect changes + validate + commit+push. Append-only is intentional for safety — consolidation cycles handle pruning/merging separately. Git provides versioning and rollback. The citation tracking ("used, helpful" / "read, not applicable" / "used, outdated") enables data-driven pruning during consolidation. The feature's simplicity (just files in a git repo) is by design.

**Context:** Devin uses opaque session snapshots. Claude Code has flat CLAUDE.md. The append-only + consolidation pattern is unique and addresses the ETH Zurich finding (arXiv:2602.11988) that naive context accumulation hurts performance — consolidation prevents unbounded growth.

**Alternatives considered:** Database-backed knowledge store (loses git versioning and human inspectability), TTL-based auto-expiry (loses valuable rare-but-useful entries).

**Reassess when:** The feature moves from experimental to production-stable, or if brain size becomes a measurable performance degradation factor (would indicate consolidation frequency needs increasing).

---

### Image extraction: security hardening kept but feature is experimental, may be reworked

**Date:** 2026-07-25
**Category:** scope

**Decision:** The image extraction feature (IssueImageExtractor + ImageDownloadService) includes substantial security hardening (SSRF protection, magic bytes validation, dimension bounds, atomic byte budget, throughput floor detection). The hardening was agent-recommended during implementation — not from a deliberate security design session. It's kept because it costs nothing in normal operation and protects against real attack vectors (SSRF via issue markdown). However, the feature is experimental and not fully tested — parts may be reworked. Security parameters are reasonable defaults, not hardened invariants with a formal threat model.

**Context:** No comparable agent system downloads issue images for agent consumption. The feature enables handling visual bugs (screenshots, UI mockups). The security depth is disproportionate to the feature's maturity — but removing it would be regression.

**Alternatives considered:** No image support (simpler but loses visual context), unsecured download (unacceptable for user-controlled URLs).

**Reassess when:** Feature is fully tested and promoted to production-stable — formalize security parameters as proper invariants. Or when rework reveals the hardening causes issues.

---

## Future Direction

<!-- Decisions about what IS and IS NOT planned, scope boundaries -->

### Three deployment modes: Legacy → DB+SignalR → DB+Kubernetes (progressive)

**Date:** 2026-07-04
**Category:** future-direction

**Decision:** The system supports three deployment modes representing progressive infrastructure investment. Legacy (in-memory JSON files, zero dependencies) was the initial implementation. DB+SignalR adds Postgres persistence for multi-replica safety. DB+Kubernetes adds K8s Job-based dispatch for production scale. For non-K8s deployments, DB+SignalR is the production path. K8s-only is a possible long-term direction but that decision hasn't been made yet. Legacy mode remains for zero-friction onboarding but is not guaranteed feature parity with DB modes — new persistence-dependent features can be DB-only.

**Status (2026-08-16):** Spec 041 removed Legacy mode and DB+SignalR mode. Kubernetes (DB+Kubernetes) is now the only supported deployment target. PostgreSQL is required. The progressive model was collapsed to a single mode.

**Context:** The `IWorkDistributor` and `IConfigurationStore` abstractions enable all three modes. Docker Compose is the development/small-team target. Helm chart is the K8s production target. Both deployment targets are first-class. New features requiring work item lifecycle or reconciliation can be DB-only.

**Alternatives considered:** K8s-only (locks out non-K8s users), single mode (loses progressive adoption), deprecate Legacy immediately.

**Reassess when:** The decision to go K8s-only becomes clear (likely after CRD-based dispatch proves itself in production), or if Legacy mode maintenance becomes a test burden without active users.

---

### MaxDecompositionSubIssueFiles=12: research-based, low-confidence default

**Date:** 2026-08-14
**Category:** configuration

**Decision:** `MaxDecompositionSubIssueFiles = 12` (valid range 1–30, `[ProjectOverridable]`). The value was raised from 5 based on research showing agents routinely handle 10–15 file changes (the consolidation loop demonstrates this empirically). It was NOT chosen via A/B testing on decomposed sub-issues — the decomposition feature is rarely used, so no usage data exists yet. The default should be kept at 12 with low confidence; it is the best available estimate, not an empirically validated optimum. If `agent:error` or `agent:wont-do` rates on decomposed sub-issues become measurable, calibrate from that data.

**Context:** Previous default was 5 (too conservative — artificially fragmented refactors). The upper bound of 30 is a safety rail, not a recommendation. Per-project override allows teams with complex codebases to lower the limit.

**Alternatives considered:** Keep at 5 (overly conservative), increase to 20+ (no evidence agents succeed at that scope on decomposed sub-issues), per-template override instead of per-project (no demand yet).

**Reassess when:** Decomposition feature accumulates enough runs (50+) to compute `agent:done` vs `agent:error`/`agent:wont-do` rates on sub-issues. If error rate increases with file count, lower the default.

---

### MaxConsolidationDispatchRetries: promoted to PipelineConfiguration — resolved by #2025

**Date:** 2026-08-14 · **Closed:** 2026-08-22
**Category:** configuration

**Decision:** `MaxConsolidationDispatchRetries` is now a `PipelineConfiguration` property `[Key(74)]` with `[ProjectOverridable(Order=30)]` and a nullable per-project override in `PipelineProject`. Default value is 5. #2025 complete. No behavioral change.

**Reassess when:** Never — once fixed, this decision is stable.

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

### Prompt architecture: layered composition with non-overridable structural guardrails

**Date:** 2026-07-25
**Category:** architecture

**Decision:** Prompts follow a strict 3-layer architecture: `DefaultPrompts.cs` (raw content) → `PipelineConfiguration` (overridable per-project) → `PromptBuilder` (composes final prompt with non-overridable structural elements). The structural elements — scope fences, thoroughness footer, calibration footer, verification clause — are hardcoded in PromptBuilder because they're research-backed behavioral guardrails, not domain content. Users customize WHAT the agent does (analysis focus areas, review checklist items). The pipeline controls HOW it behaves (primacy-positioned scope fences, debiasing calibration, anti-fabrication verification). Making structural elements configurable is explicitly out of scope — the pipeline flow is strict by design.

**Context:** Most agent frameworks (LangChain, CrewAI) use template engines for composition. The hardcoded structural approach matches Anthropic's recommended architecture for production agents (separate system-level behavioral constraints from user-level content). C# string constants mean prompt changes require recompile — acceptable because prompts change at deploy cadence. Research references: arXiv:2508.12358 (over-rejection bias → calibration), arXiv:2605.01771 (compliance gap → verification clause brevity), arXiv:2603.18740 (developer framing bias → debiasing).

**Alternatives considered:** Template engine for all elements (loses guaranteed behavioral compliance), external prompt files with hot-reload (premature for single-operator system), DSPy-style auto-optimization (research-grade, not production-ready).

**Reassess when:** Never for the structural/domain separation principle. If the system serves multiple teams who need different calibration thresholds, consider making calibration tunable while keeping scope fences immutable.

---

### Acceptance criteria parsing: regex-based, intentionally simple and deterministic

**Date:** 2026-07-25
**Category:** architecture

**Decision:** `IssueDescriptionParser` uses regex to extract `## Acceptance Criteria` sections (checkbox `- [ ]` or numbered `1.` items) from markdown issue bodies. No NLP, no LLM-based parsing. This is intentionally simple because: (1) the format is standard across all `IIssueProvider` implementations (GitHub, GitLab), (2) deterministic parsing never hallucinates criteria that don't exist, (3) it works reliably for the expected input format. The dedicated AC evaluation agent then assesses compliance, producing structured JSON. NonCompliant results are injected as CRITICAL findings, forcing the implementation agent to address them or exhaust the retry budget.

**Context:** Devin and OpenHands don't parse acceptance criteria structurally. The approach is novel — closest research is requirement-to-test traceability (arXiv:2507.02564, ~73.7% recall). The regex approach trades recall for precision. Issues without an AC section degrade gracefully to "check issue goals."

**Alternatives considered:** LLM-based extraction for non-standard formatting (adds latency and hallucination risk), more permissive patterns (risk extracting non-criteria content as criteria).

**Reassess when:** If a new IIssueProvider uses a fundamentally different format where regex can't extract criteria, or if false-negative rate on criteria extraction becomes measurable.

---

### NonCompliant acceptance criteria as CRITICAL: hard contract, retry budget bounds cost

**Date:** 2026-07-25
**Category:** architecture

**Decision:** Acceptance criteria are part of both DoR (Definition of Ready) and DoD (Definition of Done). When the AC agent reports `NonCompliant`, it's injected as a `[CRITICAL]` finding that forces the implementation agent to retry. If after all retry attempts the AC remains non-compliant, the PR ships with non-compliant status visible — this is an acceptable outcome. The implementing agent being "sure" after multiple rounds that the AC can't be met is valid signal (the criterion may be unfeasible or the AC agent may be wrong). The retry budget bounds the cost of false negatives.

**Context:** No comparable system treats AC as a forcing function. Most systems treat AC as documentation. This approach is closer to contract testing (Pact) where contract violations are hard failures. The bounded retry budget prevents infinite loops from AC agent false negatives.

**Alternatives considered:** Soften to WARNING (loses the forcing function — AC becomes advisory), confidence threshold (adds complexity without clear benefit given bounded retries).

**Reassess when:** If the AC agent's false-negative rate becomes high enough that retry budgets are regularly exhausted on correct implementations. Track via: ratio of "all AC compliant" PRs vs "non-compliant after retries" PRs.

---

### Feedback loop: data collection only, automated calibration explicitly deferred

**Date:** 2026-07-25
**Category:** scope

**Decision:** The system collects structured feedback from every run (success AND failure) via `FeedbackPromptBuilder`. `HarnessSuggestionExecutor` periodically analyzes accumulated feedback and produces actionable suggestions. `RefactoringOutcomeLookback` tracks wont-do vs done rates. NO automated adjustment exists — feedback is collected, analyzed, surfaced to the human operator, and the human decides what to change. This is intentional: no clear design exists for safe automated calibration, and premature automation risks compounding errors. The arXiv:2607.13091 "accumulated behavioral rules" approach is interesting but too new for production adoption.

**Context:** Most production systems (GitHub Copilot, Cursor) don't have automated self-calibration. Research (arXiv:2607.13091, July 2026) proposes closed-loop self-improvement but is unproven in production. The data collection infrastructure is complete and ready for a future automation layer when a safe design emerges.

**Alternatives considered:** Auto-disable proposals if rejection rate > 50% (simple but loses nuance), auto-create issues from suggestions (adds noise without human validation), full closed-loop per arXiv:2607.13091 (too experimental).

**Reassess when:** A clear, safe design for automated calibration emerges — likely triggered by accumulated data showing stable, predictable feedback patterns that could be auto-acted upon without human oversight.

---

### Prompt versioning: out of scope, scale doesn't justify the infrastructure

**Date:** 2026-07-25
**Category:** scope

**Decision:** Prompts are tracked via git history only. No version numbers, changelogs, A/B testing, or evaluation pipelines exist. Changes are immediate on deploy (all runs use new prompts). This is acceptable because: (1) prompts change infrequently, (2) adversarial review catches prompt-induced regressions, (3) single operator can revert quickly, (4) the system doesn't serve multiple teams with different prompt needs.

**Context:** Production LLM systems at scale (Anthropic enterprise, Scale AI) use explicit prompt versioning with evaluation pipelines. Research (Thomas Wiegold 2026) shows versioning becomes critical when multiple teams share prompt infrastructure. The "prompts are just code" approach works for single-team systems.

**Alternatives considered:** Content hash in telemetry (enables Grafana correlation but adds complexity for a problem that doesn't exist), semantic versions on constants (documentation overhead without consumer).

**Reassess when:** The system serves multiple teams, OR a regression is traced to a prompt change that took days to identify because there was no correlation in telemetry.

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

**Decision:** The Agent Coding page is exclusively for configuring templates and dispatching work. It does NOT show pipeline progress, output terminals, or run summaries. That was wrongly implemented (likely a leftover from before the remote agent model existed) and has been removed (#1059). The page always shows the same view regardless of whether runs are active — template table, loop controls, manual dispatch. Pipeline observation belongs on Agent Monitoring.

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

**Date:** 2026-07-04 · **Updated:** 2026-08-22
**Category:** ux

**Decision:** The Agent Monitoring page polls at 5-second intervals. Issue #1058 implemented the change from 2s → 5s. The freshness indicator transparency ("Refreshing every 5s") is intentional — it builds trust that data is current.

**Context:** Grafana defaults to 5-second refresh. GitHub Actions uses 5-10s.

**Alternatives considered:** Event-driven via SignalR (eliminates polling entirely — future possibility), configurable interval (over-engineering for single-operator use).

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

### Pod anti-affinity: soft (preferred) spreading, not hard (required)

**Date:** 2026-08-28
**Category:** architecture

**Decision:** All control-plane Deployments (api, orchestrator, jobcontroller, scheduler) use `preferredDuringSchedulingIgnoredDuringExecution` for pod anti-affinity when `replicas > 1`. The previously committed `requiredDuringSchedulingIgnoredDuringExecution` was incorrect — it blocks pod scheduling when the cluster has fewer nodes than replicas (e.g., 2 replicas on a 1-node dev cluster → `Pending` forever). Soft spreading achieves HA in production without preventing scheduling on constrained clusters.

**Context:** Hard anti-affinity is appropriate for stateful services (databases, Kafka) where co-location causes data corruption. These are stateless HTTP services; co-location reduces HA but does not corrupt state. All four templates now use weight=100 `preferred` spreading on `kubernetes.io/hostname`. The `orchestrator.affinity` override (user-provided) still takes precedence when set.

**Alternatives considered:** Hard anti-affinity (rejected — blocks dev single-node clusters), no anti-affinity (less HA in prod).

**Reassess when:** Never for the soft/hard choice on stateless services. If a specific deployment requires hard node isolation (regulatory requirement), the `affinity:` values override handles it.

---

### AgentJobTimeoutSeconds: unified wall-clock ceiling for all job types — backstop semantics for chat pods

**Date:** 2026-08-28
**Category:** configuration

**Decision:** `AgentJobTimeoutSeconds` (renamed from `ChatSessionMaxDurationSeconds`, default 7200s) governs `activeDeadlineSeconds` for all K8s Job types: work-item agents, consolidation agents, and chat pods. The rename makes the semantics correct — the previous name was misleading because the field always applied to all jobs, not only chat. For chat pods, the circuit-based idle-kill mechanism (`ChatIdleTimeoutSeconds=90s`) terminates the pod when the browser window closes; `AgentJobTimeoutSeconds` is a last-resort backstop for orphaned resources (e.g., browser crash with no idle-kill firing, Redis unavailable for heartbeat cross-replica delivery).

**Context:** The idle-kill circuit is the primary chat lifecycle mechanism. The 2h wall clock only fires if the circuit fails. Keeping a single unified timeout value simplifies operations: operators only need to reason about one "max job lifetime" per agent type, not separate interactive vs batch limits.

**Alternatives considered:** Separate `ChatSessionMaxDurationSeconds` for interactive vs batch jobs — rejected because the idle-kill circuit makes the distinction unnecessary for normal operation; edge cases (orphaned pods) benefit from the same backstop regardless of job type.

**Reassess when:** If users routinely need chat sessions >2h, add a separate `ChatSessionMaxDurationSeconds` that defaults to longer than `AgentJobTimeoutSeconds`. Track via: how often chat pods hit `activeDeadlineSeconds` vs idle-kill.

---

### Chat keepalive Redis: required for multi-replica deployments

**Date:** 2026-08-28
**Category:** architecture

**Decision:** Redis is required when `api.replicas > 1` OR `orchestrator.replicas > 1` for correct chat keepalive behavior. When keepalive POSTs land on a different API replica than the one hosting the watcher, the in-process `LastClientHeartbeatTicks` fallback does NOT see the heartbeat — the pod is idle-killed after `ChatIdleTimeoutSeconds` (90s) even though the browser is active. Redis is the cross-replica authoritative heartbeat store (`chat:heartbeat:{agentId}`, TTL=2× idle timeout). The in-process fallback is only correct for single-replica local dev.

A startup warning must be added when `ChatJobDispatcher` is instantiated with `_redis == null` and `api.replicas > 1`. This is tracked in #2133.

**Context:** The current code silently falls back to local ticks when Redis is absent — no warning is emitted. In a 2-replica deployment without Redis configured, a user whose keepalive POST happens to land on the non-watcher replica will see their chat pod killed after 90s of inactivity on the watcher side. This is a silent incorrect behavior, not a loud failure.

**Alternatives considered:** Make Redis unconditionally required (breaks local dev without Redis), configurable `requireRedisForHeartbeat: true/false` (unnecessary complexity when the rule is simply `replicas > 1`). See #2131 for implementation.

**Reassess when:** Never for the principle. Redis is already required for multi-replica SignalR backplane — this is consistent with that existing requirement.

---

### ExternalCiDuration vs PostPrCiDuration: two separate histograms for two distinct CI poll phases

**Date:** 2026-08-28
**Category:** architecture

**Decision:** `ExternalCiDuration` (existing) measures the pre-PR CI poll in `AppendExternalCiIfNeededAsync` — triggered on branch push. A new `PostPrCiDuration` histogram must be added to measure the post-PR CI poll in `WaitForPostPrCiAsync` — triggered on the pull_request event after PR promotion. The two are semantically distinct: pre-PR CI validates the branch commit; post-PR CI validates CI workflows that only trigger on `pull_request` events. Emitting both into `ExternalCiDuration` inflates p50/p99 dashboards on runs that execute both phases.

**Context:** The TODO in `WaitForPostPrCiAsync` documents this gap. On paths where both pre-PR and post-PR CI run (e.g., cleanup made no changes → skipCiIfNoChanges → post-PR CI fires), two histogram samples land in `ExternalCiDuration`, making the p99 appear 2× higher than actual worst-case single-phase duration.

**Alternatives considered:** Keep single metric with a phase tag (`phase=pre_pr|post_pr`) — equally valid but requires a Grafana filter to disaggregate; separate histograms are more natural for Grafana dashboard panels that show one thing per panel. Run-level aggregation (`TotalCiWaitDuration` emitted once at end) — accurate but loses per-phase granularity needed for debugging.

**Reassess when:** Never for the separation principle. If a third CI poll phase is added, follow the same pattern.

---

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
- "Label swap: add-first ordering" scoped by "Token vending: private keys never leave orchestrator" (both assume imperfect external APIs)
- "External CI re-push" scoped by "Partial failure contract" (CI is on the critical path — failure is retried, not ignored)
- "Project overrides: deep-merge (#1044 resolved)" constrains "No schema versioning" (merge requires distinguishing "not set" from "set to default")
- "LocalPipelineExecutor: accidental monolith" correlates with "Agent lifetime dual model" (executor grew as both modes added features)
- "AgentCoding component ↔ PageService boundary" scoped by "PipelineService event handling" (event state transitions should migrate to PageService per the boundary principle)
- "Undo snackbar: always show" correlates with "Error messages: sticky with dismiss" (both are feedback pattern decisions — success/undo are transient, errors are persistent)
- "Drawer tabs: three-component approach" scoped by "AgentCoding component ↔ PageService boundary" (drawer state lives in PageService, rendering in components)
- "Target user: single operator" scopes "Information density: high for monitoring" (operator expertise justifies dense dashboard)
- "Target user: single operator" scopes "Monitoring refresh: 5-second polling" (single operator means low server load regardless)
- "Visual design: dark-first" scopes "Information density: high for monitoring" (dark theme with purple accent designed for dense data display)
- "Interaction model: mouse-first" constrains "Keyboard shortcuts" (shortcuts are bonus, not primary interaction path)
- "Pipeline progress: dual-panel" scoped by "Target user: single operator" (desktop-assumed, screen-width-leveraging layout)
- "Refactoring consolidation loop: autonomous up to PR" scoped by "Adversarial review is default pattern" (review is the quality gate that enables autonomy)
- "Refactoring auto-dispatch with dependencies" scoped by "Refactoring proposal quality bar" (only proposals passing the quality bar get dependency chains)
- "Value types / dispatch decomposition: follow DDD" scoped by "No schema versioning" (value types need wire-compat awareness)

- "Prompt architecture: layered composition" scoped by "Adversarial review is default pattern" (structural guardrails enable reliable adversarial review behavior)
- "AC parsing: regex-based" enables "NonCompliant AC as CRITICAL" (parsing feeds the AC evaluation agent which produces the CRITICAL findings)
- "NonCompliant AC as CRITICAL" scoped by "MaxRetries=3 arbitrary default" (retry budget bounds the cost of AC false negatives)
- "Feedback loop: data collection only" correlates with "Refactoring proposal quality bar" (outcome tracking informs quality bar but doesn't auto-adjust it)
- "Prompt versioning: out of scope" scoped by "Target user: single operator" (multi-team would require versioning)
- "Code review iteration: CRITICAL-only re-review" scoped by "Adversarial review is default pattern" (review agents produce severity-graded findings that drive the decision tree)
- "Code review iteration: CRITICAL-only re-review" correlates with "Cleanup step before PR" (cleanup handles cosmetic issues that warnings might leave)
- "Epic decomposition: two-phase with human gate" scoped by "Refactoring auto-dispatch with dependencies" (simple refactoring is autonomous; complex epics require human gate)
- "PR creation: draft-first then finalize" enables "Pipeline progress: dual-panel" (draft PR provides visibility during execution)
- "Brain knowledge: experimental, append-only" scoped by "Filesystem-as-context" (brain is delivered to agents via filesystem like all other context)
- "Image extraction: experimental with security hardening" scoped by "Token vending: private keys never leave orchestrator" (both reflect defense-in-depth philosophy)

- "Dispatch priority: static ordering Review > Decomp > Impl > Consolidation" supersedes "Dispatch fairness: equal round-robin" (session 10 correction)
- "Housekeeping auto-update concurrency: 1 is permanent default" scoped by "Agent lifetime: pull→push evolution" (serial default fits current deployment's serial merge queue)
- "GitHub mergeability mapping: conservative null for unknown states" scoped by "Housekeeping auto-update concurrency" (null-state concurrency slot semantics are the correctness invariant the feature depends on)
- "DispatchGatedLabels: extensible set for human-approval-required transitions" scoped by "Label lifecycle needs formalization (#1046)" (gated labels are one axis of the label state machine)
- "DispatchGatedLabels: extensible" correlates with "Epic decomposition: two-phase with human gate" (EpicApproved is currently the only gated label, but the set is designed for future approval gates)
- "MaxDecompositionSubIssueFiles=12: research-based low-confidence" scoped by "Epic decomposition: two-phase with human gate" (sub-issue scope constraint operationalizes 'achievable in one agent run')
- "MaxConsolidationDispatchRetries → #2025" constrains "Dispatch priority: static ordering" (consolidation is lowest priority; its retry mechanism must be consistent with other priority-tier retry config)

- "PipelineLoopService: full loop leader-gated" scoped by "Agent lifetime: pull→push evolution" (all deployments are K8s; the loop runs unconditionally only in test environments without leader election)
- "PipelineLoopService: full loop leader-gated" enables "Housekeeping auto-update concurrency: 1 is permanent default" (concurrency gate only works correctly when a single leader runs the poll loop)
- "DatabaseMaintenanceService: migrate to LeaderElectedPollingService" scoped by "PipelineLoopService: full loop leader-gated" (same principle: all background loops with side-effects must be leader-gated in multi-replica)
- "DatabaseMaintenanceService: migrate to LeaderElectedPollingService" correlates with "LeaderElectedPollingService: two correctness defects (#2027)" (migration can now proceed — #2027 is resolved; both the null-check and OCE filter are fixed)
- "AgentCodingPageService: extract per-drawer orchestrators" scoped by "AgentCoding component ↔ PageService boundary" (drawer orchestration is the next extraction target after the component boundary was established)
- "DrawerCancellationToken: wrong-token bug (#2028)" scoped by "AgentCodingPageService: extract per-drawer orchestrators" (bug is a symptom of the tight coupling; extraction removes the need for the property entirely)
- "LeaderElectedPollingService: two correctness defects (#2027)" — resolved; constraint on PipelineLoopService migration lifted
- "ProviderConfigId validation: no strong opinion" scoped by "Dual JSON options (Default/Lenient)" (both decisions follow the principle: strict at boundaries the system controls, lenient at agent-produced boundaries)

- "LoopStatePersistenceService: no leader guard needed" scoped by "PipelineLoopService: full loop leader-gated" (the loop gate makes eager activation on all replicas safe — all sit in leader-wait until promoted)
- "LoopStatePersistenceService: no leader guard needed" correlates with "Agent lifetime: pull→push evolution" (90s delay is a rolling-deploy drain guard, not a multi-replica coordination mechanism)

- "Pod anti-affinity: soft spreading" scoped by "Monolithic orchestrator is intentional" (stateless services; co-location reduces HA but doesn't corrupt state)
- "AgentJobTimeoutSeconds: unified backstop" scoped by "Agent lifetime: pull→push evolution" (K8s-only; both work-item and chat pods are ephemeral K8s Jobs)
- "AgentJobTimeoutSeconds: unified backstop" enables "Chat keepalive Redis required for multi-replica" (the backstop fires when the idle-kill circuit fails, which happens when Redis is absent in multi-replica)
- "Chat keepalive Redis required for multi-replica" scoped by "HMAC key derivation for agent auth" (Redis is already in the dependency stack for multi-replica SignalR; this adds one more reason it's required)
- "ExternalCiDuration vs PostPrCiDuration" scoped by "Telemetry philosophy: instrument every decision point" (the split follows from the principle that each observable event gets its own instrument)

### Coverage Gaps (auto-detected)
- Automated calibration design remains explicitly deferred
- Housekeeping feature calibration data — no empirical data yet; revisit after 50+ housekeeping cycles
- AgentCodingPageService decomposition — decision captured but implementation not yet started
- `RateLimiter` null-forgiving operator (#1994) — no opinion captured, implementation detail

### Queued Questions (for next session)
- Automated calibration design — when a clear mechanism emerges, revisit
- Housekeeping calibration: after 50+ branch-update cycles, is concurrency=1 still correct?
- AgentCodingPageService decomposition: after extraction, was the per-drawer split the right granularity?
- PostPrCiDuration: after #2133 and the metric split are implemented, verify Grafana panels updated

---

### HeartbeatMonitorService: deleted at 041–045 arc close (T1, arch-audit 2026-08-22)

**Date:** 2026-08-22
**Category:** architecture

**Decision (Option B — delete):** `HeartbeatMonitorService` and its six sweep phases (`ChatAgentSweepPhase`, `StaleHeartbeatSweepPhase`, `OrphanRestoredJobSweepPhase`, `ProgressTimeoutSweepPhase`, `DisconnectedAgentSweepPhase`, `OrphanedRunSweepPhase`) were deleted. The source files are gone; the empty `SweepPhases/` directory was removed.

**Rationale:** The K8s topology (agents are ephemeral one-shot pods, not a long-lived fleet) makes several sweep phases hazardous:
- `DisconnectedAgentSweepPhase` would fail runs for pods that completed and exited normally.
- `StaleHeartbeatSweepPhase` and `ProgressTimeoutSweepPhase` duplicate work already done by `ReconciliationService` in JobController.
- Only `OrphanRestoredJobSweepPhase` had no JobController equivalent — but `AgentOrphanRecoveryService` now logs a warning and ReconciliationService's work-item timeout fires within `ReconciliationTimeoutSeconds` (default: 3600s), which is acceptable.

**What covers timeout enforcement now:**
- **Work-item timeouts:** `ReconciliationService` + `ReconciliationLoop` in `CodingAgentWebUI.JobController`.
- **Orphan detection/restoration:** `AgentOrphanRecoveryService` (in Hub, fires on agent re-registration).
- **Stale-heartbeat / disconnect state cleanup:** Not enforced in the agent registry — agents are ephemeral. A pod that exits simply stops sending heartbeats; its registry entry ages out naturally when the reconciler terminates its work item.

**`OrphanRestoredAt` field retained:** The field and its writers in `AgentOrphanRecoveryService`, `AgentHub.Lifecycle`, `AgentJobLifecycleService`, and `RunLifecycleManager` remain live. They gate the "should we fail this run if agent goes silent?" logic inside `AgentOrphanRecoveryService`'s crash-recovery path and are cleared on successful job completion.

**T4 test guard:** `LayerBoundaryTests.AllBackgroundServices_AreRegisteredOrRetired` includes `HeartbeatMonitorService` in its retired allowlist with a "DELETED — do not add back" comment. The guard will fail if the class is ever re-introduced without a corresponding `AddHostedService` call.

---

### AgentSelectorKey: canonical label-to-selector serialization (T22, arch-audit 2026-08-22)

**Date:** 2026-08-22
**Category:** architecture

**Decision:** Extracted `AgentSelectorKey.From(IEnumerable<string>? labels)` into `CodingAgentWebUI.Pipeline.Models.AgentSelectorKey`. It normalises a label list into the comma-separated, ordinally-sorted string stored in `WorkItemEntity.AgentSelector` and `JobDistributionRequest.AgentSelector`.

**Rationale:** Two callers (`ConsolidationDispatchService.cs` and `ConsolidationRehydrationExtensions.cs`) had byte-identical logic that had already co-changed 4 times. Divergence in sort order or separator would cause agent selection to silently return `null` — the lookup in `JobDeduplicationGuardService.SelectAgent` uses the same serialization to build the candidate key. A difference causes a silent no-match rather than a compile error. Centralising makes the invariant visible.

**Reassess when:** A third call site appears, or the separator changes (both must move together).

### ScanPagedAsync: shared over-fetch paging loop for PostgresPipelineRunHistoryService (T22, arch-audit 2026-08-22)

**Date:** 2026-08-22
**Category:** architecture

**Decision:** Extracted `private ScanPagedAsync(db, page, pageSize, fetchBatch, include, ct)` in `PostgresPipelineRunHistoryService`. The two paged-scan methods (`GetRunHistoryPagedInternalAsync` and `GetRunHistoryPagedWithFeedbackFilterInternalAsync`) differed only by their fetch delegate (LINQ vs `FromSqlRaw`) and an optional extra predicate for feedback-only. Three shared bugfix commits had already touched both.

**Reassess when:** A third paged query with a different fetch strategy is added — extend the same helper.

### Agent mode naming: ChatMode and WorkItemMode replace SignalRMode and K8sMode (T23, arch-audit 2026-08-22)

**Date:** 2026-08-22
**Category:** naming / architecture

**Decision:** `AgentSignalRModeRegistration` → `AgentChatModeRegistration`; `AgentK8SModeRegistration` → `AgentWorkItemModeRegistration`.

**Rationale:** The old names were actively misleading — both modes use SignalR and both run in Kubernetes. The essential difference is:

- **Work-item mode** (one-shot batch): pod owns a durable `WorkItem` row; must drive it to a terminal status; uses `AgentConnectionManager`, `WorkItemAgentService`, and `IJobCompletionReporter`.
- **Chat mode** (long-lived interactive): pod owns no durable row; product is streamed output; uses `AgentConnectionLifecycle`, `AgentWorkerService`, `ChatJobHandler`, and `CriticalMessageBuffer`.

**The split is deliberate and should not be re-unified.** Registration overlap between the two files is 0% — not one registration line is shared. Three of ~10 slots fill the same abstraction with a genuinely different implementation (e.g., `IJobCompletionReporter` vs `SignalRCompletionReporter`). Merging them behind an `if` would be strictly worse.

**Dead branch removed at same time:** The `!isChatMode` check at `AgentWorkerService.cs:94` is the correct live path (wires `OnAssignJob` for work-item pods). Confirmed live — not removed.
