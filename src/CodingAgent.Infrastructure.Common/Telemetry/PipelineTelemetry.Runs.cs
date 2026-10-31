using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
    // ── Run lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Counter: terminal pipeline run outcomes, recorded once per transition in the API.
    /// Tags: run_type, outcome (closed set), failure_reason, pipeline.project_name.
    /// </summary>
    public static readonly Counter<long> RunOutcomes;

    /// <summary>
    /// Histogram: total pipeline run duration (Dispatched → terminal), in seconds.
    /// Buckets sized for human-scale job durations (1 min → 12 h).
    /// </summary>
    public static readonly Histogram<double> RunDuration;

    /// <summary>
    /// Histogram: per-step duration recorded by the API on each step transition.
    /// Tags: run_type, step (PipelineStep name).
    /// Recorded once per step visit in <c>HandleStepTransition</c>.
    /// </summary>
    public static readonly Histogram<double> RunStepDuration;

    /// <summary>
    /// Counter: sub-issues created or failed per decomposition run. Recorded once per run.
    /// Tags: result (created / failed).
    /// </summary>
    public static readonly Counter<long> RunSubIssues;

    /// <summary>
    /// Counter: brain update result per run (pushed / none). Recorded once per non-consolidation run.
    /// Tags: result (pushed / none / failed). Recorded only for runs with a brain provider.
    /// </summary>
    // TODO: [WARNING] Unit is UnitRun = "{run}" (confirmed correct per OTel spec). If this constant is
    // ever changed (e.g. to "run" without braces), the Prometheus exporter will emit a non-standard unit
    // annotation. RunSubIssues uses "{issue}" — the unit mismatch between the two new counters is
    // intentional per the requirements table. See review findings [WARNING] DotNetSpecialist L87.
    public static readonly Counter<long> RunBrainUpdates;

    /// <summary>
    /// Counter: brain skills injection result per run (injected / skipped / failed / unknown).
    /// Recorded once per non-consolidation run with a brain provider. Recorded by: API.
    /// Tags: result (injected / skipped / failed / unknown), run_type.
    /// </summary>
    public static readonly Counter<long> RunBrainSkills;

    // ── LLM usage counters (API-side, recorded at terminal time) ────────────────────────────────
    // All four counters are tagged: run_type, phase, provider.
    // pipeline.run.agent_sessions also includes: model.
    // Phase is a closed set — see NormalizeRunPhase().

    /// <summary>
    /// Counter: total tokens consumed per run, broken down by phase, provider and model.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider, model.
    /// </summary>
    public static readonly Counter<long> RunTokens;

    /// <summary>
    /// Counter: total LLM cost in USD per run, broken down by phase, provider and model.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider, model.
    /// </summary>
    public static readonly Counter<double> RunCostUsd;

    /// <summary>
    /// Counter: number of agent CLI sessions (invocations) per run, broken down by phase, provider, and model.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider, model.
    /// </summary>
    public static readonly Counter<long> RunAgentSessions;

    /// <summary>
    /// Counter: total agent execution time in seconds per run, broken down by phase, provider and model.
    /// Recorded once per run at terminal status time by the API.
    /// Tags: run_type, phase, provider, model.
    /// </summary>
    public static readonly Counter<double> RunAgentTime;

    // ── Detailed LLM usage (API-side, recorded at terminal time from the per-phase breakdown) ────
    // These carry no phase tag, to keep series counts bounded; per-phase totals stay on the
    // counters above.

    /// <summary>
    /// Counter: tokens consumed per run, split by token type.
    /// Tags: run_type, provider, token_type (<see cref="TokenTypes"/>).
    /// </summary>
    public static readonly Counter<long> RunTokenUsage;

    /// <summary>
    /// Counter: agent turns (model round trips) per run, from providers that report them.
    /// Tags: run_type, provider.
    /// </summary>
    public static readonly Counter<long> RunAgentTurns;

    /// <summary>
    /// Counter: web search requests the model made per run, from providers that report them.
    /// Tags: run_type, provider.
    /// </summary>
    public static readonly Counter<long> RunWebSearchRequests;

    /// <summary>
    /// Counter: provider-reported LLM cost in USD per run, split by how the calls were paid for.
    /// billing=api is billed per token; billing=subscription is an estimate under a flat plan.
    /// Tags: run_type, provider, billing (<see cref="AgentBillingModes"/>).
    /// </summary>
    public static readonly Counter<double> RunBillingCostUsd;

    /// <summary>
    /// Counter: subscription rate-limit readings reported at run end (latest per window).
    /// Tags: provider, window, status.
    /// </summary>
    public static readonly Counter<long> RunRateLimitEvents;

    /// <summary>
    /// Histogram: fraction (0–1) of a subscription rate-limit window used, as last seen in a run.
    /// Tags: provider, window, status.
    /// </summary>
    public static readonly Histogram<double> RunRateLimitUtilization;

    // ── API-side quality gate and CI metrics (issue #2979) ───────────────────────────────────────

    /// <summary>
    /// Counter: individual gate evaluation outcomes, recorded by the API when ReportQualityGateResult
    /// arrives from the agent. Tags: run_type, gate (compilation|tests|external_ci),
    /// result (pass|fail), infrastructure_failure (true|false).
    /// </summary>
    public static readonly Counter<long> RunQualityGateResults;

    /// <summary>
    /// Counter: CI re-trigger commits (empty commit + push to restart CI that never started).
    /// Recorded by the API when the agent reports a re-trigger event via ReportPipelineRunEvent.
    /// Tags: run_type.
    /// </summary>
    public static readonly Counter<long> RunCiNotStartedRetriggers;

    /// <summary>
    /// Histogram: time from push to CI conclusion, recorded by the API when the agent reports
    /// the CI wait duration via ReportPipelineRunEvent.
    /// Tags: run_type, stage (pre_pr|post_pr), result (pass|fail).
    /// Buckets sized for CI pipeline wait times (1 min → 4 h).
    /// </summary>
    public static readonly Histogram<double> RunCiWait;

    // ── Static constructor ───────────────────────────────────────────────────────────────────────
    // Placed in Runs.cs (the largest partial file) so that it executes after the Meter and
    // ActivitySource field initializers in the root PipelineTelemetry.cs. The static constructor
    // body always runs after all static field initializers across all partial files; the only
    // field initializers in this type are Meter = new(SourceName) and ActivitySource = new(SourceName)
    // in the root file, plus TraceContextPropagator = new() in Propagation.cs — none of which
    // depend on Meter — so Meter is guaranteed to be set before any Meter.CreateX<>() call here.

    static PipelineTelemetry()
    {
        // ── Run lifecycle ────────────────────────────────────────────────────────
        RunOutcomes = Meter.CreateCounter<long>(
            "pipeline.run.outcomes", UnitRun, "Terminal pipeline run outcome counts");
        RunDuration = Meter.CreateHistogram<double>(
            "pipeline.run.duration", "s", "Total duration of pipeline runs from dispatch to terminal status",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200]
            });
        RunStepDuration = Meter.CreateHistogram<double>(
            "pipeline.run.step.duration", "s", "Duration of each pipeline step (API-recorded)",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800]
            });
        RunSubIssues = Meter.CreateCounter<long>(
            "pipeline.run.sub_issues", "{issue}", "Sub-issue creation results per decomposition run");
        RunBrainUpdates = Meter.CreateCounter<long>(
            "pipeline.run.brain_updates", UnitRun, "Brain update results at run completion");
        RunBrainSkills = Meter.CreateCounter<long>(
            "pipeline.run.brain_skills", UnitRun, "Brain skills injection results per run");

        // ── LLM usage ────────────────────────────────────────────────────────────
        RunTokens = Meter.CreateCounter<long>(
            "pipeline.run.tokens", "{token}", "LLM tokens consumed per pipeline run phase");
        RunCostUsd = Meter.CreateCounter<double>(
            "pipeline.run.cost_usd", "{usd}", "LLM cost in USD per pipeline run phase");
        RunAgentSessions = Meter.CreateCounter<long>(
            "pipeline.run.agent_sessions", "{session}", "Agent CLI sessions per pipeline run phase");
        RunAgentTime = Meter.CreateCounter<double>(
            "pipeline.run.agent_time", "s", "Agent execution time in seconds per pipeline run phase");
        RunTokenUsage = Meter.CreateCounter<long>(
            "pipeline.run.token_usage", "{token}", "LLM tokens consumed per pipeline run, by token type");
        RunAgentTurns = Meter.CreateCounter<long>(
            "pipeline.run.agent_turns", "{turn}", "Agent turns (model round trips) per pipeline run");
        RunWebSearchRequests = Meter.CreateCounter<long>(
            "pipeline.run.web_search_requests", "{request}", "Web search requests made by the model per pipeline run");
        RunBillingCostUsd = Meter.CreateCounter<double>(
            "pipeline.run.billing_cost_usd", "{usd}", "LLM cost in USD per pipeline run, by billing mode");
        RunRateLimitEvents = Meter.CreateCounter<long>(
            "pipeline.run.rate_limit_events", UnitEvent, "Subscription rate-limit readings per pipeline run, by window and status");
        RunRateLimitUtilization = Meter.CreateHistogram<double>(
            "pipeline.run.rate_limit_utilization", "1", "Fraction of the subscription rate-limit window used, as last seen in a run",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 1.0]
            });

        // ── Quality gate and CI ──────────────────────────────────────────────────
        RunQualityGateResults = Meter.CreateCounter<long>(
            "pipeline.run.quality_gate.results", "{evaluation}",
            "Quality gate evaluation outcomes recorded by the API at time of result");
        RunCiNotStartedRetriggers = Meter.CreateCounter<long>(
            "pipeline.run.ci.not_started_retriggers", "{retrigger}",
            "CI re-trigger commits (empty push) fired when CI never started");
        RunCiWait = Meter.CreateHistogram<double>(
            "pipeline.run.ci.wait", "s",
            "Time from push to CI conclusion (pre-PR and post-PR)",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [60, 300, 600, 900, 1800, 3600, 7200, 14400]
            });

        // ── Brain ────────────────────────────────────────────────────────────────
        BrainReviewLines = Meter.CreateCounter<long>(
            "pipeline.brain.review.lines", "{line}", "Skill lines a brain review promoted, appended, retired or marked missed again");
        BrainReviewRefusals = Meter.CreateCounter<long>(
            "pipeline.brain.review.refusals", UnitRun, "Brain reviews refused by validation");

        // ── Housekeeping ─────────────────────────────────────────────────────────
        HousekeepingTriggered = Meter.CreateCounter<long>(
            "pipeline.housekeeping.triggered", UnitUpdate, "Server-side branch updates triggered");
        HousekeepingSucceeded = Meter.CreateCounter<long>(
            "pipeline.housekeeping.succeeded", UnitUpdate, "Server-side branch updates completed successfully");
        HousekeepingFailed = Meter.CreateCounter<long>(
            "pipeline.housekeeping.failed", UnitUpdate, "Server-side branch updates that threw an exception");
        HousekeepingSkipped = Meter.CreateCounter<long>(
            "pipeline.housekeeping.skipped", UnitUpdate,
            "PRs skipped for policy reasons during candidate selection, tagged by skip_reason (active_run | in_flight | cooldown)");
        HousekeepingEvicted = Meter.CreateCounter<long>(
            "pipeline.housekeeping.evicted", UnitUpdate, "In-flight entries removed (CI resolved or PR merged/label removed)");
        HousekeepingConflictReworkTriggered = Meter.CreateCounter<long>(
            "pipeline.housekeeping.conflict_rework_triggered", "{rework}",
            "Issues re-queued for rework due to PR merge conflict");
        HousekeepingBranchDeleted = Meter.CreateCounter<long>(
            "pipeline.housekeeping.branch_deleted", "{branch}",
            "Stale agent branches deleted (no open PR, inactive issue label)");
        PullRequestsClosed = Meter.CreateCounter<long>(
            "pipeline.pull_requests.closed", "{pr}",
            "Pull requests closed (merged or closed without merge) during housekeeping cycles");
        PullRequestTimeToMerge = Meter.CreateHistogram<double>(
            "pipeline.pull_requests.time_to_merge", "s",
            "Time from pull request creation to merge",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [300, 600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 345600, 604800]
            });
        HousekeepingSlotExhausted = Meter.CreateCounter<long>(
            "pipeline.housekeeping.slot_exhausted", "{exhaustion}",
            "Housekeeping cycles where the in-flight slot limit was reached for a repository");
        HousekeepingPrEvaluated = Meter.CreateCounter<long>(
            "pipeline.housekeeping.pr_evaluated", "{pr}",
            "Pull requests evaluated per mergeability status during housekeeping cycles");
        HousekeepingReprobeTriggered = Meter.CreateCounter<long>(
            "pipeline.housekeeping.reprobe_triggered", UnitReprobe,
            "Re-probe passes fired for PRs whose first mergeability probe returned Unknown (GitHub/GitLab lazy-compute workaround)");
        HousekeepingReprobeResolved = Meter.CreateCounter<long>(
            "pipeline.housekeeping.reprobe_resolved", UnitReprobe,
            "PRs that resolved to a non-Unknown mergeability state on re-probe (tagged by resolved_state)");
        LabelSwapRemoveExhausted = Meter.CreateCounter<long>(
            "label_swap_remove_exhausted_total", "{exhaustion}",
            "Count of remove-phase retry exhaustions in AgentLabelOperations.SwapAsync (throwOnRemoveExhaustion=false path). " +
            "Indicates a dual-label state requiring operator attention. " +
            "The identifier tag carries issue/PR identifiers (e.g. org/repo#123); cardinality is bounded in practice since the counter fires only on error paths.");
        QueueSweepCancelled = Meter.CreateCounter<long>(
            "pipeline.queue_sweep.cancelled", UnitItem, "WorkItems cancelled as stale by the queue sweep");
        QueueSweepSkipped = Meter.CreateCounter<long>(
            "pipeline.queue_sweep.skipped", UnitItem, "WorkItems skipped by the queue sweep (fail-open: provider not polled, rate-limited, wrong TaskType)");
        QueueSweepFailed = Meter.CreateCounter<long>(
            "pipeline.queue_sweep.failed", UnitItem, "PostStatusAsync unexpected failures during queue sweep (expected races like already-terminal are not counted)");
        HubAuthRejections = Meter.CreateCounter<long>(
            "agent.hub.auth_rejections", "{rejection}",
            "Hub-auth rejections by reason (reconnect_race | not_registered | job_mismatch)");

        // ── Routing and dispatch ─────────────────────────────────────────────────
        RoutingRefusals = Meter.CreateCounter<long>(
            "pipeline.routing.refusals", "{refusal}",
            "Agent routing refusals by task type, stage (create, dispatch, claim) and reason");
        ConsolidationDispatchPermanentFailures = Meter.CreateCounter<long>(
            "consolidation.dispatch.permanent_failures", UnitFailure,
            "Consolidation dispatch permanent failures (e.g. no job template for selector). Tagged by run.type.");
        TokenVendingFailures = Meter.CreateCounter<long>(
            "token_vending.failures", UnitFailure, "Token vending failures");
        LoopPolls = Meter.CreateCounter<long>(
            "pipeline.loop.polls", "{poll}", "Pipeline loop poll attempts");
        LoopIssuesFound = Meter.CreateCounter<long>(
            "pipeline.loop.issues_found", "{issue}", "Issues found per poll cycle");
        LoopDispatchDecisions = Meter.CreateCounter<long>(
            "pipeline.loop.dispatch_decisions", "{decision}", "Dispatch decisions made by the loop");
        LoopBackoffEvents = Meter.CreateCounter<long>(
            "pipeline.loop.backoff_events", UnitEvent, "Backoff escalations due to poll failures");
        LoopCircuitBreakerTrips = Meter.CreateCounter<long>(
            "pipeline.loop.circuit_breaker_trips", "{trip}", "Circuit breaker trip events");
        DispatchLinkedIssueFetchFailed = Meter.CreateCounter<long>(
            "pipeline.dispatch.linked_issue_fetch_failed", UnitFailure,
            "Failed attempts to fetch linked issue context during dispatch enrichment (non-fatal)");
        DispatchLinkedIssuesResolved = Meter.CreateCounter<long>(
            "pipeline.dispatch.linked_issues_resolved", "{issue}",
            "Linked issues successfully resolved during dispatch enrichment, by source");
        RunAgentStalls = Meter.CreateCounter<long>(
            "pipeline.run.agent_stalls", "{stall}",
            "Agent stall events (stall_kill, process_death, process_timeout) recorded by the API");

        // ── Observable gauges (must be last — gauge instruments are created on the Meter) ───────
        // ObservableGauge instruments must be created on the Meter; creating them in the static
        // constructor avoids duplicate-instrument warnings if the type is loaded by multiple hosts.
        Meter.CreateObservableGauge(
            "agent.jobs.active",
            observeValues: () => Volatile.Read(ref _activeJobsCallback)?.Invoke() ?? Enumerable.Empty<Measurement<long>>(),
            unit: "{job}",
            description: "Currently executing agent jobs");
        Meter.CreateObservableGauge(
            "agent.connections.total",
            observeValues: () => Volatile.Read(ref _totalConnectionsCallback)?.Invoke() ?? Enumerable.Empty<Measurement<long>>(),
            unit: "{connection}",
            description: "Total registered agents");
    }

    // ── Normalization helpers ────────────────────────────────────────────────────

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
        ArgumentNullException.ThrowIfNull(ex);
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

    // ── Nested tag classes ───────────────────────────────────────────────────────

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

        /// <summary>All closed-set phase values.</summary>
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

        /// <summary>All closed-set provider values.</summary>
        public static readonly string[] All = [Kiro, OpenCode, Claude, Unknown];
    }

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
    /// Closed-set stage values for <c>pipeline.run.ci.wait</c> stage tag.
    /// </summary>
    public static class CiWaitStages
    {
        public const string PrePr = "pre_pr";
        public const string PostPr = "post_pr";
        public static readonly string[] All = [PrePr, PostPr];

        /// <summary>
        /// Maps an agent-reported CI wait stage to the closed set, or <c>null</c> when unknown,
        /// so a misbehaving agent cannot inflate label cardinality.
        /// </summary>
        public static string? Normalize(string? stage) =>
            All.FirstOrDefault(s => s.Equals(stage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Closed-set result values for <c>pipeline.run.ci.wait</c> result tag.
    /// </summary>
    public static class CiWaitResults
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        public static readonly string[] All = [Pass, Fail];

        /// <summary>
        /// Maps an agent-reported CI wait result to the closed set, or <c>null</c> when unknown,
        /// so a misbehaving agent cannot inflate label cardinality.
        /// </summary>
        public static string? Normalize(string? result) =>
            All.FirstOrDefault(r => r.Equals(result, StringComparison.OrdinalIgnoreCase));
    }
}
