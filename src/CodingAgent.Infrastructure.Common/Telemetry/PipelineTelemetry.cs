using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace CodingAgent.Pipeline.Telemetry;

/// <summary>
/// Central telemetry definitions for the pipeline. Provides an <see cref="ActivitySource"/>
/// for distributed tracing and a <see cref="Meter"/> for metrics.
/// </summary>
public static class PipelineTelemetry
{
    public const string SourceName = "CodingAgent.Pipeline";

    // UCUM-style unit annotation constants shared across counter definitions.
    // Defined here to avoid S1192 (repeated string literals) and to make unit
    // semantics explicit at the call site.
    private const string UnitUpdate = "{update}";
    private const string UnitFailure = "{failure}";
    private const string UnitItem = "{item}";
    private const string UnitRun = "{run}";
    private const string UnitEvent = "{event}";
    private const string UnitReprobe = "{reprobe}";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// Counter: terminal pipeline run outcomes, recorded once per transition in the API.
    /// Tags: run_type, outcome (closed set), failure_reason, pipeline.project_name.
    /// Pre-initialized at process start for the 3 closed-tag dimensions (run_type × outcome × failure_reason)
    /// so that <c>increase()</c> is visible from the first event after a deploy for those dimensions.
    /// <c>pipeline.project_name</c> is excluded from pre-initialization (unbounded cardinality) per Requirement 7.
    /// </summary>
    public static readonly Counter<long> RunOutcomes = Meter.CreateCounter<long>(
        "pipeline.run.outcomes", UnitRun, "Terminal pipeline run outcome counts");

    /// <summary>
    /// Histogram: total pipeline run duration (Dispatched → terminal), in seconds.
    /// Buckets sized for human-scale job durations (1 min → 12 h).
    /// </summary>
    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "pipeline.run.duration", "s", "Total duration of pipeline runs from dispatch to terminal status",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200]
        });

    /// <summary>
    /// Histogram: per-step duration recorded by the API on each step transition.
    /// Tags: run_type, step (PipelineStep name).
    /// Not pre-initialized (histograms cannot be pre-initialized).
    /// Recorded once per step visit in <c>HandleStepTransition</c>.
    /// </summary>
    public static readonly Histogram<double> RunStepDuration = Meter.CreateHistogram<double>(
        "pipeline.run.step.duration", "s", "Duration of each pipeline step (API-recorded)",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800]
        });

    /// <summary>
    /// Counter: sub-issues created or failed per decomposition run. Recorded once per run.
    /// Tags: result (created / failed).
    /// Pre-initialized in <c>Program.EmitPreInitCounters</c> for all 2 tag combinations.
    /// </summary>
    public static readonly Counter<long> RunSubIssues = Meter.CreateCounter<long>(
        "pipeline.run.sub_issues", "{issue}", "Sub-issue creation results per decomposition run");

    /// <summary>
    /// Counter: brain update result per run (pushed / none). Recorded once per non-consolidation run.
    /// Tags: result (pushed / none).
    /// Pre-initialized in <c>Program.EmitPreInitCounters</c> for all 2 tag combinations.
    /// </summary>
    // TODO: [WARNING] Unit is UnitRun = "{run}" (confirmed correct per OTel spec). If this constant is
    // ever changed (e.g. to "run" without braces), the Prometheus exporter will emit a non-standard unit
    // annotation. RunSubIssues uses "{issue}" — the unit mismatch between the two new counters is
    // intentional per the requirements table. See review findings [WARNING] DotNetSpecialist L87.
    public static readonly Counter<long> RunBrainUpdates = Meter.CreateCounter<long>(
        "pipeline.run.brain_updates", UnitRun, "Brain update results at run completion");

    // ── LLM usage counters (API-side, recorded at terminal time) ────────────────────────────────
    // All four counters are tagged: run_type, phase, provider.
    // pipeline.run.agent_sessions also includes: model.
    // Phase is a closed set — see NormalizeRunPhase().

    /// <summary>
    /// Counter: total tokens consumed per run, broken down by phase and provider.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider.
    /// Pre-initialized at process start for run_type × phase × provider combinations.
    /// </summary>
    public static readonly Counter<long> RunTokens = Meter.CreateCounter<long>(
        "pipeline.run.tokens", "{token}", "LLM tokens consumed per pipeline run phase");

    /// <summary>
    /// Counter: total LLM cost in USD per run, broken down by phase and provider.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider.
    /// Pre-initialized at process start for run_type × phase × provider combinations.
    /// </summary>
    public static readonly Counter<double> RunCostUsd = Meter.CreateCounter<double>(
        "pipeline.run.cost_usd", "{usd}", "LLM cost in USD per pipeline run phase");

    /// <summary>
    /// Counter: number of agent CLI sessions (invocations) per run, broken down by phase, provider, and model.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider, model.
    /// Pre-initialized at process start for run_type × phase × provider (model excluded per Req 7).
    /// </summary>
    public static readonly Counter<long> RunAgentSessions = Meter.CreateCounter<long>(
        "pipeline.run.agent_sessions", "{session}", "Agent CLI sessions per pipeline run phase");

    /// <summary>
    /// Counter: total agent execution time in seconds per run, broken down by phase and provider.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider.
    /// Pre-initialized at process start for run_type × phase × provider combinations.
    /// </summary>
    public static readonly Counter<double> RunAgentTime = Meter.CreateCounter<double>(
        "pipeline.run.agent_time", "s", "Agent execution time in seconds per pipeline run phase");

    // ── Detailed LLM usage (API-side, recorded at terminal time from the per-phase breakdown) ────
    // These carry no phase tag, to keep series counts bounded; per-phase totals stay on the
    // counters above.

    /// <summary>
    /// Counter: tokens consumed per run, split by token type.
    /// Tags: run_type, provider, token_type (<see cref="TokenTypes"/>).
    /// Pre-initialized at process start for run_type × provider × token_type.
    /// </summary>
    public static readonly Counter<long> RunTokenUsage = Meter.CreateCounter<long>(
        "pipeline.run.token_usage", "{token}", "LLM tokens consumed per pipeline run, by token type");

    /// <summary>
    /// Counter: agent turns (model round trips) per run, from providers that report them.
    /// Tags: run_type, provider. Pre-initialized at process start.
    /// </summary>
    public static readonly Counter<long> RunAgentTurns = Meter.CreateCounter<long>(
        "pipeline.run.agent_turns", "{turn}", "Agent turns (model round trips) per pipeline run");

    /// <summary>
    /// Counter: web search requests the model made per run, from providers that report them.
    /// Tags: run_type, provider. Pre-initialized at process start.
    /// </summary>
    public static readonly Counter<long> RunWebSearchRequests = Meter.CreateCounter<long>(
        "pipeline.run.web_search_requests", "{request}", "Web search requests made by the model per pipeline run");

    /// <summary>
    /// Counter: provider-reported LLM cost in USD per run, split by how the calls were paid for.
    /// billing=api is billed per token; billing=subscription is an estimate under a flat plan.
    /// Tags: run_type, provider, billing (<see cref="AgentBillingModes"/>). Pre-initialized at process start.
    /// </summary>
    public static readonly Counter<double> RunBillingCostUsd = Meter.CreateCounter<double>(
        "pipeline.run.billing_cost_usd", "{usd}", "LLM cost in USD per pipeline run, by billing mode");

    /// <summary>
    /// Counter: subscription rate-limit readings reported at run end (latest per window).
    /// Tags: provider, window, status. Pre-initialized for the claude provider.
    /// </summary>
    public static readonly Counter<long> RunRateLimitEvents = Meter.CreateCounter<long>(
        "pipeline.run.rate_limit_events", UnitEvent, "Subscription rate-limit readings per pipeline run, by window and status");

    /// <summary>
    /// Histogram: fraction (0–1) of a subscription rate-limit window used, as last seen in a run.
    /// Tags: provider, window, status.
    /// </summary>
    public static readonly Histogram<double> RunRateLimitUtilization = Meter.CreateHistogram<double>(
        "pipeline.run.rate_limit_utilization", "1", "Fraction of the subscription rate-limit window used, as last seen in a run",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 1.0]
        });

    /// <summary>
    /// Normalizes a raw phase key (from <see cref="RunMetrics.PhaseBreakdown"/>, an agent invocation, or an
    /// agent-reported stall event) to the closed <see cref="RunPhases"/> set. This one vocabulary is used by
    /// the <c>phase</c> tag of every metric and by the <c>pipeline.phase</c> attribute of
    /// <c>invoke_agent</c> spans. Per-reviewer keys (<c>review_{name}</c>, <c>follow_up_{name}</c>) collapse
    /// into <c>review</c>; unknown keys map to <c>other</c>.
    /// </summary>
    /// <remarks>
    /// Also accepts the stall phase names older agent images still report
    /// (<c>qgc_retry_agent</c>, <c>code_review</c>, <c>unknown</c>).
    /// </remarks>
    public static string NormalizeRunPhase(string? phase)
    {
        if (string.IsNullOrEmpty(phase))
            return RunPhases.Other;

        var key = phase.ToLowerInvariant();
        return key switch
        {
            "analysis" => RunPhases.Analysis,
            "analysis_review" or "analysisreview" => RunPhases.AnalysisReview,
            "codegen" or "code_gen" or "code generation" => RunPhases.CodeGen,
            "review" or "code_review" or "fix" => RunPhases.Review,
            "quality_gate" or "qgc_retry_agent" => RunPhases.QualityGate,
            "acceptance_criteria" or "acceptancecriteria" => RunPhases.AcceptanceCriteria,
            "pr_description" or "prdescription" => RunPhases.PrDescription,
            "reflection" => RunPhases.Reflection,
            _ when key.StartsWith("decomposition", StringComparison.Ordinal) => RunPhases.Decomposition,
            _ when key.StartsWith("review_", StringComparison.Ordinal)
                || key.StartsWith("review ", StringComparison.Ordinal)
                || key.StartsWith("follow_up_", StringComparison.Ordinal) => RunPhases.Review,
            _ => RunPhases.Other
        };
    }

    /// <summary>
    /// Normalized phase tag values, shared by the <c>pipeline.run.*</c> usage counters,
    /// <c>pipeline.run.agent_stalls</c> and the <c>pipeline.phase</c> span attribute.
    /// </summary>
    public static class RunPhases
    {
        public const string Analysis = "analysis";
        public const string AnalysisReview = "analysis_review";
        public const string CodeGen = "codegen";
        /// <summary>Code review: reviewer agents, follow-ups, the summary, and the fix iterations.</summary>
        public const string Review = "review";
        /// <summary>Quality gate retries: the fix agent runs and the gate processes themselves.</summary>
        public const string QualityGate = "quality_gate";
        public const string AcceptanceCriteria = "acceptance_criteria";
        public const string PrDescription = "pr_description";
        public const string Reflection = "reflection";
        public const string Decomposition = "decomposition";
        public const string Other = "other";

        /// <summary>All closed-set phase values for pre-initialization.</summary>
        public static readonly string[] All =
        [
            Analysis, AnalysisReview, CodeGen, Review, QualityGate, AcceptanceCriteria,
            PrDescription, Reflection, Decomposition, Other
        ];
    }

    /// <summary>Normalized provider tag values for pipeline.run.* LLM usage counters.</summary>
    public static class RunProviders
    {
        public const string Kiro = "kiro";
        public const string OpenCode = "opencode";
        public const string Claude = "claude";
        public const string Unknown = "unknown";

        /// <summary>All closed-set provider values for pre-initialization.</summary>
        public static readonly string[] All = [Kiro, OpenCode, Claude, Unknown];
    }

    /// <summary>
    /// Maps an agent-reported provider name to the <see cref="RunProviders"/> closed set, so a
    /// misbehaving agent cannot inflate label cardinality. Unknown or missing values become "unknown".
    /// </summary>
    public static string NormalizeRunProvider(string? provider) =>
        RunProviders.All.FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase))
        ?? RunProviders.Unknown;

    /// <summary>Maps a reported billing mode to the <see cref="AgentBillingModes"/> closed set.</summary>
    public static string NormalizeBillingMode(string? billingMode) =>
        AgentBillingModes.All.FirstOrDefault(b => b.Equals(billingMode, StringComparison.OrdinalIgnoreCase))
        ?? AgentBillingModes.Unknown;

    /// <summary>Token type tag values for <see cref="RunTokenUsage"/>.</summary>
    public static class TokenTypes
    {
        public const string Input = "input";
        public const string Output = "output";
        public const string Reasoning = "reasoning";
        public const string CacheRead = "cache_read";
        public const string CacheWrite = "cache_write";

        public static readonly string[] All = [Input, Output, Reasoning, CacheRead, CacheWrite];
    }

    /// <summary>
    /// Window and status tag values for the rate-limit metrics. Values outside these sets are
    /// recorded as "other" so a new CLI version cannot inflate label cardinality.
    /// </summary>
    public static class RateLimitTags
    {
        public const string Other = "other";

        public static readonly string[] Windows = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "overage"];
        public static readonly string[] Statuses = ["allowed", "allowed_warning", "rejected"];

        public static string NormalizeWindow(string? window) =>
            Windows.FirstOrDefault(w => w.Equals(window, StringComparison.OrdinalIgnoreCase)) ?? Other;

        public static string NormalizeStatus(string? status) =>
            Statuses.FirstOrDefault(s => s.Equals(status, StringComparison.OrdinalIgnoreCase)) ?? Other;
    }

    // ── API-side quality gate and CI metrics (issue #2979) ───────────────────────────────────────
    // These are recorded by the API when the agent reports events via the hub. Agent pods export no
    // metrics at all (issue #2980), so anything an agent measures must reach the API this way.

    /// <summary>
    /// Counter: individual gate evaluation outcomes, recorded by the API when ReportQualityGateResult
    /// arrives from the agent. Tags: run_type, gate (compilation|tests|external_ci),
    /// result (pass|fail), infrastructure_failure (true|false).
    /// Pre-initialized at process start for closed tag combinations.
    /// </summary>
    public static readonly Counter<long> RunQualityGateResults = Meter.CreateCounter<long>(
        "pipeline.run.quality_gate.results", "{evaluation}",
        "Quality gate evaluation outcomes recorded by the API at time of result");

    /// <summary>
    /// Counter: CI re-trigger commits (empty commit + push to restart CI that never started).
    /// Recorded by the API when the agent reports a re-trigger event via ReportPipelineRunEvent.
    /// Tags: run_type.
    /// Pre-initialized at process start.
    /// </summary>
    public static readonly Counter<long> RunCiNotStartedRetriggers = Meter.CreateCounter<long>(
        "pipeline.run.ci.not_started_retriggers", "{retrigger}",
        "CI re-trigger commits (empty push) fired when CI never started");

    /// <summary>
    /// Histogram: time from push to CI conclusion, recorded by the API when the agent reports
    /// the CI wait duration via ReportPipelineRunEvent.
    /// Tags: run_type, stage (pre_pr|post_pr), result (pass|fail).
    /// Buckets sized for CI pipeline wait times (1 min → 4 h).
    /// Not pre-initialized (histograms cannot be pre-initialized).
    /// </summary>
    public static readonly Histogram<double> RunCiWait = Meter.CreateHistogram<double>(
        "pipeline.run.ci.wait", "s",
        "Time from push to CI conclusion (pre-PR and post-PR)",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [60, 300, 600, 900, 1800, 3600, 7200, 14400]
        });

    /// <summary>
    /// Counter: agent stall events recorded by the API when the agent reports a stall event
    /// via ReportPipelineRunEvent. Tags: run_type, phase, kind (stall_kill|process_death|process_timeout).
    /// Pre-initialized at process start for phase × kind combinations.
    /// </summary>
    public static readonly Counter<long> RunAgentStalls = Meter.CreateCounter<long>(
        "pipeline.run.agent_stalls", "{stall}",
        "Agent stall events (stall_kill, process_death, process_timeout) recorded by the API");

    public static readonly Counter<long> ConsolidationDispatchPermanentFailures = Meter.CreateCounter<long>(
        "consolidation.dispatch.permanent_failures", UnitFailure,
        "Consolidation dispatch permanent failures (e.g. no job template for selector). Tagged by run.type.");

    // Token vending metrics
    public static readonly Counter<long> TokenVendingFailures = Meter.CreateCounter<long>(
        "token_vending.failures", UnitFailure, "Token vending failures");

    // Loop metrics
    public static readonly Counter<long> LoopPolls = Meter.CreateCounter<long>(
        "pipeline.loop.polls", "{poll}", "Pipeline loop poll attempts");
    public static readonly Counter<long> LoopIssuesFound = Meter.CreateCounter<long>(
        "pipeline.loop.issues_found", "{issue}", "Issues found per poll cycle");
    public static readonly Counter<long> LoopDispatchDecisions = Meter.CreateCounter<long>(
        "pipeline.loop.dispatch_decisions", "{decision}", "Dispatch decisions made by the loop");
    public static readonly Counter<long> LoopBackoffEvents = Meter.CreateCounter<long>(
        "pipeline.loop.backoff_events", UnitEvent, "Backoff escalations due to poll failures");
    public static readonly Counter<long> LoopCircuitBreakerTrips = Meter.CreateCounter<long>(
        "pipeline.loop.circuit_breaker_trips", "{trip}", "Circuit breaker trip events");

    // Dispatch linked-issue metrics
    /// <summary>
    /// Counter: failures fetching a linked issue context during dispatch enrichment (non-fatal).
    /// Each increment corresponds to one issue reference that could not be resolved.
    /// </summary>
    public static readonly Counter<long> DispatchLinkedIssueFetchFailed = Meter.CreateCounter<long>(
        "pipeline.dispatch.linked_issue_fetch_failed", UnitFailure,
        "Failed attempts to fetch linked issue context during dispatch enrichment (non-fatal)");

    /// <summary>
    /// Counter: linked issues successfully resolved during dispatch enrichment.
    /// Tags: source (closing_keyword | issue_url).
    /// </summary>
    public static readonly Counter<long> DispatchLinkedIssuesResolved = Meter.CreateCounter<long>(
        "pipeline.dispatch.linked_issues_resolved", "{issue}",
        "Linked issues successfully resolved during dispatch enrichment, by source");

    /// <summary>
    /// Closed-set tag values for <c>pipeline.dispatch.linked_issues_resolved</c> source tag.
    /// </summary>
    public static class LinkedIssueSource
    {
        /// <summary>Issue reference resolved via closing keyword syntax (e.g. "Closes #123").</summary>
        public const string ClosingKeyword = "closing_keyword";
        /// <summary>Issue reference resolved via direct URL in issue body.</summary>
        public const string IssueUrl = "issue_url";
    }

    // Housekeeping metrics
    public static readonly Counter<long> HousekeepingTriggered = Meter.CreateCounter<long>(
        "pipeline.housekeeping.triggered", UnitUpdate, "Server-side branch updates triggered");
    public static readonly Counter<long> HousekeepingSucceeded = Meter.CreateCounter<long>(
        "pipeline.housekeeping.succeeded", UnitUpdate, "Server-side branch updates completed successfully");
    public static readonly Counter<long> HousekeepingFailed = Meter.CreateCounter<long>(
        "pipeline.housekeeping.failed", UnitUpdate, "Server-side branch updates that threw an exception");
    public static readonly Counter<long> HousekeepingSkipped = Meter.CreateCounter<long>(
        "pipeline.housekeeping.skipped", UnitUpdate,
        "PRs skipped for policy reasons during candidate selection, tagged by skip_reason (active_run | in_flight | cooldown)");
    public static readonly Counter<long> HousekeepingEvicted = Meter.CreateCounter<long>(
        "pipeline.housekeeping.evicted", UnitUpdate, "In-flight entries removed (CI resolved or PR merged/label removed)");
    public static readonly Counter<long> HousekeepingConflictReworkTriggered = Meter.CreateCounter<long>(
        "pipeline.housekeeping.conflict_rework_triggered", "{rework}",
        "Issues re-queued for rework due to PR merge conflict");
    public static readonly Counter<long> HousekeepingBranchDeleted = Meter.CreateCounter<long>(
        "pipeline.housekeeping.branch_deleted", "{branch}",
        "Stale agent branches deleted (no open PR, inactive issue label)");

    /// <summary>
    /// Counter: PRs that have been closed (merged or closed-without-merge) during a housekeeping cycle.
    /// Tags: outcome (merged | closed_unmerged).
    /// Pre-initialized at startup via <c>GitHubTelemetry.PreInitialize()</c>.
    /// </summary>
    public static readonly Counter<long> PullRequestsClosed = Meter.CreateCounter<long>(
        "pipeline.pull_requests.closed", "{pr}",
        "Pull requests closed (merged or closed without merge) during housekeeping cycles");

    /// <summary>
    /// Histogram: time from PR creation to merge, in seconds.
    /// Only recorded when outcome is "merged" and createdAt is available from the PR creation cache.
    /// </summary>
    public static readonly Histogram<double> PullRequestTimeToMerge = Meter.CreateHistogram<double>(
        "pipeline.pull_requests.time_to_merge", "s",
        "Time from pull request creation to merge",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [300, 600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 345600, 604800]
        });

    /// <summary>
    /// Counter: housekeeping slot-exhaustion events (in-flight limit reached for a repo).
    /// Tags: repo_provider_id.
    /// </summary>
    public static readonly Counter<long> HousekeepingSlotExhausted = Meter.CreateCounter<long>(
        "pipeline.housekeeping.slot_exhausted", "{exhaustion}",
        "Housekeeping cycles where the in-flight slot limit was reached for a repository");

    /// <summary>
    /// Counter: individual PRs evaluated for mergeability status in a housekeeping cycle.
    /// Tags: repo_provider_id, mergeability_status (behind | up_to_date | conflicted | blocked | unknown).
    /// </summary>
    public static readonly Counter<long> HousekeepingPrEvaluated = Meter.CreateCounter<long>(
        "pipeline.housekeeping.pr_evaluated", "{pr}",
        "Pull requests evaluated per mergeability status during housekeeping cycles");

    /// <summary>
    /// Closed-set tag values for <c>pipeline.housekeeping.skipped</c>.
    /// Using constants prevents cardinality blowup on the skip_reason tag.
    /// </summary>
    public static class HousekeepingSkipReasons
    {
        /// <summary>PR skipped because its branch has an active pipeline run.</summary>
        public const string ActiveRun = "active_run";
        /// <summary>PR skipped because a housekeeping update for it is already in-flight.</summary>
        public const string InFlight = "in_flight";
        /// <summary>PR skipped because it was triggered too recently (cooldown window).</summary>
        public const string Cooldown = "cooldown";
    }

    /// <summary>
    /// Counts re-probe batches fired for PRs whose first mergeability probe returned
    /// <c>unknown</c>. Tagged by <c>repo_provider_id</c>. Each increment represents one
    /// <c>Task.Delay</c> + re-probe pass (i.e. one cycle had at least one Unknown PR).
    /// Pair with <see cref="HousekeepingReprobeResolved"/> to measure how often re-probing
    /// actually resolves the state vs stays Unknown.
    /// </summary>
    public static readonly Counter<long> HousekeepingReprobeTriggered = Meter.CreateCounter<long>(
        "pipeline.housekeeping.reprobe_triggered", UnitReprobe,
        "Re-probe passes fired for PRs whose first mergeability probe returned Unknown (GitHub/GitLab lazy-compute workaround)");

    /// <summary>
    /// Counts individual PRs that resolved to a non-Unknown state on the re-probe pass.
    /// Tagged by <c>repo_provider_id</c> and <c>resolved_state</c> (behind | clean | dirty | blocked).
    /// A high ratio of <see cref="HousekeepingReprobeResolved"/> / <see cref="HousekeepingReprobeTriggered"/>
    /// indicates the re-probe is effective. A low ratio may indicate persistent API latency (GitHub or GitLab).
    /// </summary>
    public static readonly Counter<long> HousekeepingReprobeResolved = Meter.CreateCounter<long>(
        "pipeline.housekeeping.reprobe_resolved", UnitReprobe,
        "PRs that resolved to a non-Unknown mergeability state on re-probe (tagged by resolved_state)");

    // Label swap metrics
    // TODO: The unit string "{exhaustion}" is inconsistent with the "{item}", "{retry}", "{failure}", "{event}"
    // convention used by every other counter in this file. UCUM annotation strings are free-form so this is not
    // a runtime defect, but OTLP backends that normalise unit labels may render it differently from surrounding
    // counters. Consider renaming to "{event}" or "{exhaustion_event}" to align with project conventions.
    public static readonly Counter<long> LabelSwapRemoveExhausted = Meter.CreateCounter<long>(
        "label_swap_remove_exhausted_total", "{exhaustion}",
        "Count of remove-phase retry exhaustions in AgentLabelOperations.SwapAsync (throwOnRemoveExhaustion=false path). " +
        "Indicates a dual-label state requiring operator attention. " +
        "The identifier tag carries issue/PR identifiers (e.g. org/repo#123); cardinality is bounded in practice since the counter fires only on error paths.");

    // Queue sweep metrics
    public static readonly Counter<long> QueueSweepCancelled = Meter.CreateCounter<long>(
        "pipeline.queue_sweep.cancelled", UnitItem, "WorkItems cancelled as stale by the queue sweep");
    public static readonly Counter<long> QueueSweepSkipped = Meter.CreateCounter<long>(
        "pipeline.queue_sweep.skipped", UnitItem, "WorkItems skipped by the queue sweep (fail-open: provider not polled, rate-limited, wrong TaskType)");
    public static readonly Counter<long> QueueSweepFailed = Meter.CreateCounter<long>(
        "pipeline.queue_sweep.failed", UnitItem, "PostStatusAsync unexpected failures during queue sweep (expected races like already-terminal are not counted)");

    /// <summary>
    /// Hub-auth rejection counter tagged by <c>reason</c> (closed set).
    /// Reason values: <c>reconnect_race</c>, <c>not_registered</c>,
    /// <c>job_mismatch</c>, <c>operator_forbidden</c>.
    /// </summary>
    public static readonly Counter<long> HubAuthRejections = Meter.CreateCounter<long>(
        "agent.hub.auth_rejections", "{rejection}",
        "Hub-auth rejections by reason (reconnect_race | not_registered | job_mismatch | operator_forbidden)");

    internal static class LoopDecisions
    {
        public const string Dispatched = "dispatched";
        public const string SkippedAlreadyProcessing = "skipped_already_processing";
        public const string SkippedDependencyBlocked = "skipped_dependency_blocked";
        public const string SkippedNoAgent = "skipped_no_agent";
        public const string SkippedMaxRuns = "skipped_max_runs";
        public const string SkippedFilteredByLabel = "skipped_filtered_by_label";
    }

    /// <summary>
    /// Reason tag values for <see cref="HubAuthRejections"/> (closed set — do not add cardinality).
    /// </summary>
    public static class HubAuthRejectionReasons
    {
        /// <summary>Agent connected but RegisterAgent not yet complete (reconnect-race window).</summary>
        public const string ReconnectRace = "reconnect_race";
        /// <summary>Connection ID not found in the registry at all.</summary>
        public const string NotRegistered = "not_registered";
        /// <summary>Agent is registered but the jobId does not match its active job.</summary>
        public const string JobMismatch = "job_mismatch";
        /// <summary>Operator connection tried to call an agent-only method.</summary>
        public const string OperatorForbidden = "operator_forbidden";
    }

    /// <summary>
    /// Closed-set gate name values for <c>pipeline.run.quality_gate.results</c> gate tag.
    /// </summary>
    public static class QualityGateResultGates
    {
        public const string Compilation = "compilation";
        public const string Tests = "tests";
        public const string ExternalCi = "external_ci";
        public static readonly string[] All = [Compilation, Tests, ExternalCi];
    }

    /// <summary>
    /// Closed-set kind values for <c>pipeline.run.agent_stalls</c> kind tag.
    /// </summary>
    public static class AgentStallKinds
    {
        public const string StallKill = "stall_kill";
        public const string ProcessDeath = "process_death";
        public const string ProcessTimeout = "process_timeout";
        public static readonly string[] All = [StallKill, ProcessDeath, ProcessTimeout];

        /// <summary>
        /// Maps an agent-reported stall kind to the closed set, or <c>null</c> when it is not one of
        /// the known kinds, so a misbehaving agent cannot inflate label cardinality.
        /// </summary>
        public static string? Normalize(string? kind) =>
            All.FirstOrDefault(k => k.Equals(kind, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Closed-set stage values for <c>pipeline.run.ci.wait</c> stage tag.
    /// </summary>
    public static class CiWaitStages
    {
        public const string PrePr = "pre_pr";
        public const string PostPr = "post_pr";
    }

    /// <summary>
    /// Maps a free-text agent invocation description (e.g. "Quality gate retry fix agent (attempt 2)")
    /// to a <see cref="RunPhases"/> value. Used when an invocation carries no phase key.
    /// </summary>
    public static string NormalizePhaseDescription(string phaseDescription)
    {
        ArgumentException.ThrowIfNullOrEmpty(phaseDescription);

        if (phaseDescription.Contains("Quality gate retry", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Pre-PR cleanup", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Final QG", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Post-PR CI", StringComparison.OrdinalIgnoreCase))
            return RunPhases.QualityGate;

        if (phaseDescription.Contains("Code generation", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Code gen", StringComparison.OrdinalIgnoreCase))
            return RunPhases.CodeGen;

        if (phaseDescription.Contains("Analysis agent", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.StartsWith("Analysis", StringComparison.OrdinalIgnoreCase))
            return RunPhases.Analysis;

        if (phaseDescription.Contains("Acceptance criteria", StringComparison.OrdinalIgnoreCase))
            return RunPhases.AcceptanceCriteria;

        if (phaseDescription.Contains("Code review", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Follow-up for reviewer", StringComparison.OrdinalIgnoreCase) ||
            phaseDescription.Contains("Review summary", StringComparison.OrdinalIgnoreCase))
            return RunPhases.Review;

        if (phaseDescription.Contains("Decomposition", StringComparison.OrdinalIgnoreCase))
            return RunPhases.Decomposition;

        return RunPhases.Other;
    }

    /// <summary>Creates a run_type tag from the given <see cref="PipelineRunType"/>.</summary>
    public static KeyValuePair<string, object?> RunTypeTag(PipelineRunType runType) =>
        new("run_type", runType.ToString().ToLowerInvariant());

    /// <summary>
    /// Sets project-related tags on an <see cref="Activity"/>.
    /// </summary>
    public static void SetProjectTags(Activity? activity, string? projectId, string? projectName)
    {
        activity?.SetTag("pipeline.project_id", projectId ?? ActivityTags.Unknown);
        activity?.SetTag("pipeline.project_name", projectName ?? ActivityTags.Unknown);
    }

    /// <summary>
    /// Records an error on the given <see cref="Activity"/>. For graceful cancellation
    /// (token-cancelled <see cref="OperationCanceledException"/>), sets a cancelled tag
    /// without error status. For all other exceptions, sets error status and records the exception.
    /// </summary>
    public static void RecordError(this Activity? activity, Exception ex, CancellationToken ct = default)
    {
        if (activity is null) return;
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            activity.SetTag("pipeline.cancelled", true);
            return;
        }
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddException(ex);
    }

    /// <summary>
    /// Records an error with secret-masked exception message. Use this instead of <see cref="RecordError"/>
    /// when the exception message may contain secret values from environment variables.
    /// </summary>
    public static void RecordMaskedError(this Activity? activity, Exception ex,
        IEnumerable<KeyValuePair<string, string>> secrets, CancellationToken ct = default)
    {
        if (activity is null) return;
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(secrets);
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            activity.SetTag("pipeline.cancelled", true);
            return;
        }

        var maskedMessage = SecretMasker.Mask(ex.Message, secrets);
        activity.SetStatus(ActivityStatusCode.Error, maskedMessage);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            // FullName can return null for dynamically-generated types; use fallback.
            { "exception.type", ex.GetType().FullName ?? "<unknown>" },
            { "exception.message", maskedMessage },
            // TODO: Consider masking stack trace as well for defense-in-depth against edge cases where secrets could appear.
            { "exception.stacktrace", ex.StackTrace ?? string.Empty }
        }));
    }

    private static readonly TraceContextPropagator TraceContextPropagator = new();

    /// <summary>
    /// Extracts a parent <see cref="ActivityContext"/> from a W3C trace context dictionary.
    /// Returns <see langword="default"/> when the dictionary is null or empty,
    /// causing <see cref="ActivitySource.StartActivity"/> to create a root span.
    /// </summary>
    public static ActivityContext ExtractTraceContext(Dictionary<string, string>? traceContext)
    {
        if (traceContext is not { Count: > 0 })
            return default;

        var parentContext = TraceContextPropagator.Extract(
            default,
            traceContext,
            static (c, key) => c.TryGetValue(key, out var val) ? [val] : []);
        return parentContext.ActivityContext;
    }

    /// <summary>
    /// Creates a short-lived <see cref="ActivityKind.Producer"/> span and captures its
    /// W3C trace context (traceparent + tracestate) into a dictionary suitable for serialization.
    /// When an ambient <see cref="Activity.Current"/> already exists, its context is used directly
    /// rather than spawning an intermediate span — this prevents dead-branch orphan spans in the
    /// trace backend. A new span is only minted when there is no ambient activity context.
    /// </summary>
    public static Dictionary<string, string>? CaptureTraceContext(string activityName)
    {
        // Prefer the ambient span's context directly to avoid creating a short-lived dead-branch
        // Producer span that appears as an orphan in Grafana Tempo.
        var ctx = Activity.Current?.Context ?? default;
        if (ctx == default)
        {
            using var span = ActivitySource.StartActivity(activityName, ActivityKind.Producer);
            if (span is null)
                return null;
            ctx = span.Context;
        }

        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(ctx, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.Count > 0 ? carrier : null;
    }

    /// <summary>
    /// Serializes a W3C traceparent (and tracestate when present) from an existing
    /// <see cref="Activity"/> using the OTel <see cref="TraceContextPropagator"/>.
    /// Returns the <c>traceparent</c> header value, or <see langword="null"/> when
    /// <paramref name="activity"/> is <see langword="null"/>.
    /// Prefer this over manual string formatting to ensure <c>tracestate</c> is preserved.
    /// </summary>
    public static string? FormatTraceParent(Activity? activity)
    {
        if (activity is null) return null;
        var carrier = new Dictionary<string, string>();
        TraceContextPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            carrier,
            static (c, key, value) => c[key] = value);
        return carrier.GetValueOrDefault("traceparent");
    }
}
