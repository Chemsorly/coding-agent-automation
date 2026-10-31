using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
    // ── Routing metrics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Counter: agent routing refusals (spec 053 req 9.10), recorded by the API. A refusal at creation leaves no work
    /// item, so <see cref="RunOutcomes"/> never sees it. Tags: <c>task_type</c>, <c>stage</c> (<see cref="RoutingStages"/>)
    /// and <c>reason</c> (<c>AgentRouteRefusalReason</c>). Not pre-initialized (#3639).
    /// </summary>
    public static readonly Counter<long> RoutingRefusals;

    /// <summary>Counter: consolidation dispatch permanent failures (e.g. no job template for selector). Tagged by run.type.</summary>
    public static readonly Counter<long> ConsolidationDispatchPermanentFailures;

    // ── Token vending metrics ────────────────────────────────────────────────────

    /// <summary>Counter: token vending failures.</summary>
    public static readonly Counter<long> TokenVendingFailures;

    // ── Loop metrics ─────────────────────────────────────────────────────────────

    /// <summary>Counter: pipeline loop poll attempts.</summary>
    public static readonly Counter<long> LoopPolls;

    /// <summary>Counter: issues found per poll cycle.</summary>
    public static readonly Counter<long> LoopIssuesFound;

    /// <summary>Counter: dispatch decisions made by the loop.</summary>
    public static readonly Counter<long> LoopDispatchDecisions;

    /// <summary>Counter: backoff escalations due to poll failures.</summary>
    public static readonly Counter<long> LoopBackoffEvents;

    /// <summary>Counter: circuit breaker trip events.</summary>
    public static readonly Counter<long> LoopCircuitBreakerTrips;

    // ── Dispatch linked-issue metrics ────────────────────────────────────────────

    /// <summary>
    /// Counter: failures fetching a linked issue context during dispatch enrichment (non-fatal).
    /// Each increment corresponds to one issue reference that could not be resolved.
    /// </summary>
    public static readonly Counter<long> DispatchLinkedIssueFetchFailed;

    /// <summary>
    /// Counter: linked issues successfully resolved during dispatch enrichment.
    /// Tags: source (closing_keyword | issue_url).
    /// </summary>
    public static readonly Counter<long> DispatchLinkedIssuesResolved;

    // ── Agent stall metrics ──────────────────────────────────────────────────────

    /// <summary>
    /// Counter: agent stall events recorded by the API when the agent reports a stall event
    /// via ReportPipelineRunEvent. Tags: run_type, phase, kind (stall_kill|process_death|process_timeout).
    /// </summary>
    public static readonly Counter<long> RunAgentStalls;

    // ── Routing helpers ──────────────────────────────────────────────────────────

    /// <summary>Records one routing refusal; <paramref name="reason"/> is an <c>AgentRouteRefusalReason</c> value.</summary>
    public static void RecordRoutingRefusal(WorkItemTaskType taskType, string stage, string reason) =>
        RoutingRefusals.Add(1,
            new KeyValuePair<string, object?>("task_type", taskType.ToString()),
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Sets the route attributes on <paramref name="activity"/> (the current span, when there is one).</summary>
    public static void SetRouteAttributes(Activity? activity, string profileName, string model, string effort, string source)
    {
        if (activity is null)
            return;
        activity.SetTag(RoutingAttributes.AgentProfile, profileName);
        activity.SetTag(RoutingAttributes.Model, model);
        activity.SetTag(RoutingAttributes.Effort, effort);
        activity.SetTag(RoutingAttributes.RouteSource, source);
    }

    /// <summary>Marks <paramref name="activity"/> as a routing refusal: the reason attribute and the Error status.</summary>
    public static void SetRouteRefusal(Activity? activity, string reason, string text)
    {
        if (activity is null)
            return;
        activity.SetTag(RoutingAttributes.RouteRefusal, reason);
        activity.SetStatus(ActivityStatusCode.Error, text);
    }

    // ── Nested tag classes ───────────────────────────────────────────────────────

    /// <summary>Where a route was refused: the <c>stage</c> tag of <see cref="RoutingRefusals"/>.</summary>
    public static class RoutingStages
    {
        public const string Create = "create";
        public const string Dispatch = "dispatch";
        public const string Claim = "claim";
    }

    /// <summary>The route attributes of the work item creation and claim spans (spec 053 req 9.6, 9.11).</summary>
    public static class RoutingAttributes
    {
        public const string AgentProfile = "pipeline.agent_profile";
        public const string Model = "pipeline.model";
        public const string Effort = "pipeline.effort";
        public const string RouteSource = "pipeline.route_source";
        public const string RouteRefusal = "pipeline.route_refusal";
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
    /// Closed-set tag values for <c>pipeline.dispatch.linked_issues_resolved</c> source tag.
    /// </summary>
    public static class LinkedIssueSource
    {
        /// <summary>Issue reference resolved via closing keyword syntax (e.g. "Closes #123").</summary>
        public const string ClosingKeyword = "closing_keyword";
        /// <summary>Issue reference resolved via direct URL in issue body.</summary>
        public const string IssueUrl = "issue_url";
    }

    internal static class LoopDecisions
    {
        public const string Dispatched = "dispatched";
        public const string SkippedAlreadyProcessing = "skipped_already_processing";
        public const string SkippedDependencyBlocked = "skipped_dependency_blocked";
        public const string SkippedNoAgent = "skipped_no_agent";
        public const string SkippedMaxRuns = "skipped_max_runs";
        public const string SkippedFilteredByLabel = "skipped_filtered_by_label";
    }
}
