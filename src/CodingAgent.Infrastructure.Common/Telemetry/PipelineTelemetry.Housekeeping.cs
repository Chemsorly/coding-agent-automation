using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
    // ── Housekeeping metrics ─────────────────────────────────────────────────────

    /// <summary>Counter: server-side branch updates triggered.</summary>
    public static readonly Counter<long> HousekeepingTriggered;

    /// <summary>Counter: server-side branch updates completed successfully.</summary>
    public static readonly Counter<long> HousekeepingSucceeded;

    /// <summary>Counter: server-side branch updates that threw an exception.</summary>
    public static readonly Counter<long> HousekeepingFailed;

    /// <summary>
    /// Counter: PRs skipped for policy reasons during candidate selection.
    /// Tags: skip_reason (<see cref="HousekeepingSkipReasons"/>).
    /// </summary>
    public static readonly Counter<long> HousekeepingSkipped;

    /// <summary>Counter: in-flight entries removed (CI resolved or PR merged/label removed).</summary>
    public static readonly Counter<long> HousekeepingEvicted;

    /// <summary>Counter: issues re-queued for rework due to PR merge conflict.</summary>
    public static readonly Counter<long> HousekeepingConflictReworkTriggered;

    /// <summary>Counter: stale agent branches deleted (no open PR, inactive issue label).</summary>
    public static readonly Counter<long> HousekeepingBranchDeleted;

    /// <summary>
    /// Counter: PRs that have been closed (merged or closed-without-merge) during a housekeeping cycle.
    /// Tags: outcome (merged | closed_unmerged).
    /// </summary>
    public static readonly Counter<long> PullRequestsClosed;

    /// <summary>
    /// Histogram: time from PR creation to merge, in seconds.
    /// Only recorded when outcome is "merged" and createdAt is available from the PR creation cache.
    /// </summary>
    public static readonly Histogram<double> PullRequestTimeToMerge;

    /// <summary>
    /// Counter: housekeeping slot-exhaustion events (in-flight limit reached for a repo).
    /// Tags: repo_provider_id.
    /// </summary>
    public static readonly Counter<long> HousekeepingSlotExhausted;

    /// <summary>
    /// Counter: individual PRs evaluated for mergeability status in a housekeeping cycle.
    /// Tags: repo_provider_id, mergeability_status (behind | up_to_date | conflicted | blocked | unknown).
    /// </summary>
    public static readonly Counter<long> HousekeepingPrEvaluated;

    /// <summary>
    /// Counts re-probe batches fired for PRs whose first mergeability probe returned
    /// <c>unknown</c>. Tagged by <c>repo_provider_id</c>. Each increment represents one
    /// <c>Task.Delay</c> + re-probe pass (i.e. one cycle had at least one Unknown PR).
    /// Pair with <see cref="HousekeepingReprobeResolved"/> to measure how often re-probing
    /// actually resolves the state vs stays Unknown.
    /// </summary>
    public static readonly Counter<long> HousekeepingReprobeTriggered;

    /// <summary>
    /// Counts individual PRs that resolved to a non-Unknown state on the re-probe pass.
    /// Tagged by <c>repo_provider_id</c> and <c>resolved_state</c> (behind | clean | dirty | blocked).
    /// A high ratio of <see cref="HousekeepingReprobeResolved"/> / <see cref="HousekeepingReprobeTriggered"/>
    /// indicates the re-probe is effective. A low ratio may indicate persistent API latency (GitHub or GitLab).
    /// </summary>
    public static readonly Counter<long> HousekeepingReprobeResolved;

    // Label swap metrics
    // TODO: The unit string "{exhaustion}" is inconsistent with the "{item}", "{retry}", "{failure}", "{event}"
    // convention used by every other counter in this file. UCUM annotation strings are free-form so this is not
    // a runtime defect, but OTLP backends that normalise unit labels may render it differently from surrounding
    // counters. Consider renaming to "{event}" or "{exhaustion_event}" to align with project conventions.
    /// <summary>
    /// Counter: remove-phase retry exhaustions in AgentLabelOperations.SwapAsync.
    /// Tags: identifier (issue/PR identifier, e.g. org/repo#123). Cardinality is bounded since the counter fires only on error paths.
    /// </summary>
    public static readonly Counter<long> LabelSwapRemoveExhausted;

    // ── Queue sweep metrics ──────────────────────────────────────────────────────

    /// <summary>Counter: WorkItems cancelled as stale by the queue sweep.</summary>
    public static readonly Counter<long> QueueSweepCancelled;

    /// <summary>Counter: WorkItems skipped by the queue sweep (fail-open: provider not polled, rate-limited, wrong TaskType).</summary>
    public static readonly Counter<long> QueueSweepSkipped;

    /// <summary>Counter: PostStatusAsync unexpected failures during queue sweep (expected races like already-terminal are not counted).</summary>
    public static readonly Counter<long> QueueSweepFailed;

    // ── Hub auth metrics ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hub-auth rejection counter tagged by <c>reason</c> (closed set).
    /// Reason values: <c>reconnect_race</c>, <c>not_registered</c>,
    /// <c>job_mismatch</c>.
    /// </summary>
    public static readonly Counter<long> HubAuthRejections;

    // ── Nested tag classes ───────────────────────────────────────────────────────

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
    }
}
