using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Priority-based dispatch logic across issue/PR/decomposition queues.
/// Selects the highest-priority eligible queue type each iteration: PullRequests first,
/// then Decomposition, then Issues (Implementation). Within each type, FIFO order is
/// preserved by the per-queue dequeue logic.
/// </summary>
/// <remarks>
/// Split into partial classes by work type for maintainability:
/// <list type="bullet">
///   <item><description><c>DispatchScheduler.cs</c> — shared state, constructor, orchestration loop, nested types</description></item>
///   <item><description><c>DispatchScheduler.Issues.cs</c> — issue dispatch round methods</description></item>
///   <item><description><c>DispatchScheduler.Reviews.cs</c> — PR review dispatch round methods</description></item>
///   <item><description><c>DispatchScheduler.Decomposition.cs</c> — decomposition dispatch round methods</description></item>
///   <item><description><c>DispatchScheduler.Helpers.cs</c> — shared dispatch helpers used by all round methods</description></item>
/// </list>
/// </remarks>
internal sealed partial class DispatchScheduler
{
    private const string UnknownProjectName = "Unknown";
    private readonly IDispatchRunCreator _orchestration;
    private readonly IDispatchOrchestrationService _dispatchOrchestration;
    private readonly IDependencyChecker? _dependencyChecker;
    private readonly ProviderCacheManager _cacheManager;
    private readonly Serilog.ILogger _logger;

    internal DispatchScheduler(
        IDispatchRunCreator orchestration,
        IDispatchOrchestrationService dispatchOrchestration,
        IDependencyChecker? dependencyChecker,
        ProviderCacheManager cacheManager,
        Serilog.ILogger logger)
    {
        _orchestration = orchestration;
        _dispatchOrchestration = dispatchOrchestration;
        _dependencyChecker = dependencyChecker;
        _cacheManager = cacheManager;
        _logger = logger;
    }

    /// <summary>
    /// Represents the queue type to process, in priority order (PullRequests &gt; Decomposition &gt; Issues).
    /// </summary>
    internal enum DispatchTurn { Issues = 0, PullRequests = 1, Decomposition = 2 }

    /// <summary>
    /// Result of a single-template dispatch attempt within <see cref="DispatchRoundAsync"/>.
    /// </summary>
    internal readonly record struct DispatchAttemptResult(bool Dispatched, bool Attempted = true, bool AbortRemaining = false)
    {
        /// <summary>No candidate found for this template — skip it.</summary>
        public static readonly DispatchAttemptResult Skip = new(false, Attempted: false);

        /// <summary>Abort remaining templates (e.g., concurrency limit reached).</summary>
        public static readonly DispatchAttemptResult Abort = new(false, Attempted: false, AbortRemaining: true);
    }

    /// <summary>
    /// Result of a full dispatch cycle returned to the caller.
    /// </summary>
    internal readonly record struct DispatchResult(int ProcessedCount, int FailedCount);

    /// <summary>
    /// Bundles all queue state and callbacks for a single fair round-robin dispatch cycle.
    /// Passed to <see cref="DispatchFairRoundRobinAsync"/> instead of individual parameters,
    /// eliminating the S107 excessive-parameter violation.
    /// </summary>
    internal sealed class DispatchRoundRobinRequest
    {
        /// <summary>Templates eligible to receive dispatch in this cycle.</summary>
        public required IReadOnlyList<PipelineJobTemplate> PollableTemplates { get; init; }

        /// <summary>Flattened template-project pairs for project context resolution.</summary>
        public required IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates { get; init; }

        /// <summary>Pipeline configuration for this dispatch cycle.</summary>
        public required PipelineConfiguration Config { get; init; }

        /// <summary>Maximum number of runs to dispatch in this cycle. Zero = unlimited.</summary>
        public required int MaxRunsPerCycle { get; init; }

        /// <summary>Issue identifiers already processing in this cycle (dedup).</summary>
        public required HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers { get; init; }

        /// <summary>Per-template queues of implementation issues to dispatch.</summary>
        public required Dictionary<string, List<IssueSummary>> IssueQueues { get; init; }

        /// <summary>Per-template queues of PR reviews to dispatch.</summary>
        public required Dictionary<string, List<PullRequestSummary>> PrQueues { get; init; }

        /// <summary>Per-template queues of decomposition epics to dispatch.</summary>
        public required Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> DecompositionQueues { get; init; }

        /// <summary>Per-project queues of project-level decomposition epics.</summary>
        public required Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> ProjectLevelDecompositionQueues { get; init; }

        /// <summary>Callback to report current dispatch status string.</summary>
        public required Action<string> ReportStatus { get; init; }

        /// <summary>Callback to report the current issue identifier being dispatched.</summary>
        public required Action<string?> ReportIssue { get; init; }

        /// <summary>Callback to notify UI of a state change.</summary>
        public required Action NotifyChange { get; init; }
    }

    /// <summary>
    /// Bundles template, queue, and callback context shared by all private round-dispatch helpers.
    /// </summary>
    private sealed class RoundDispatchContext
    {
        public required IReadOnlyList<PipelineJobTemplate> PollableTemplates { get; init; }
        public required HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers { get; init; }
        public required Dictionary<string, PipelineProject> TemplateProjectLookup { get; init; }
        public required Action<string?> TrackingReportIssue { get; init; }
        public required Action<string> ReportStatus { get; init; }
        public required Action NotifyChange { get; init; }
        public required int RemainingBudget { get; init; }
        public required Func<string?> GetCurrentIssueIdentifier { get; init; }
    }

    /// <summary>
    /// Fair dispatch — three-way interleaved round-robin (issues → PRs → decomposition).
    /// </summary>
    internal async Task<DispatchResult> DispatchFairRoundRobinAsync(
        DispatchRoundRobinRequest request,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int totalBudget = request.MaxRunsPerCycle > 0 ? request.MaxRunsPerCycle : int.MaxValue;

        // Compute the floor reservation: reduce the priority-loop budget by MinIssueSlots
        // so the floor pass always has budget available. The reservation only applies when:
        //   - MinIssueSlots > 0 (floor enabled)
        //   - MaxRunsPerCycle == 0 (unlimited) or >= 2 (enough budget to reserve)
        //   - Issues are actually present in the queues (no point reserving if there are no issues)
        int issueFloor = request.Config.MinIssueSlots;
        bool issuesPresent = HasEligible(request.PollableTemplates, request.IssueQueues, t => t.ImplementationEnabled);
        bool floorEnabled = issueFloor > 0
            && (request.MaxRunsPerCycle == 0 || request.MaxRunsPerCycle >= 2)
            && issuesPresent;

        // Priority-loop budget is reduced by the floor reservation, but capped at totalBudget - 1
        // (minimum 1 so the loop can still run). For unlimited budgets, no reduction needed —
        // in unlimited mode, PRs can never truly exhaust the budget; the floor is still useful
        // if the PR queue is infinite, but we handle it via the floor-fires-always path below.
        // TODO: [WARNING] Reserved floor slot(s) are wasted whenever Issues are dispatched inside
        // the priority loop (e.g. issues-only backlog, or PR queue drains mid-cycle before
        // priorityBudget is exhausted). In those cases issueDispatchedThisCycle=true causes the
        // floor pass to skip, and the reserved MinIssueSlots slots are never reclaimed, so the
        // cycle dispatches fewer items than MaxRunsPerCycle even though work remains.
        // Scenario A: MaxRunsPerCycle=5, MinIssueSlots=1, IssueQueue=10, no PRs/Decomp →
        //   priorityBudget=4, loop dispatches 4 Issues, floor skipped → 4 dispatched instead of 5.
        // Scenario B: MaxRunsPerCycle=5, MinIssueSlots=1, PRs=2, Issues=10 →
        //   loop dispatches 2 PRs then 2 Issues, floor skipped → 4 dispatched instead of 5.
        // Fix: reclaim the reserved slots when Issues are already dispatched in the priority loop
        // (e.g. top-up pass, or remove the pre-reduction and rely on post-cycle reclaim logic).
        int priorityBudget = floorEnabled && totalBudget != int.MaxValue
            ? Math.Max(totalBudget - issueFloor, 1)
            : totalBudget;

        int remaining = priorityBudget;
        int processedCount = 0;
        int failedCount = 0;
        int activeDecompositionCount = _orchestration.GetAllActiveRuns()
            .Count(r => r.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition);

        var cycleStateCache = new Dictionary<int, bool>();
        var templateProjectLookup = request.FlattenedTemplates.ToDictionary(ft => ft.Template.Id, ft => ft.Project);

        string? lastReportedIssue = null;
        var trackingReportIssue = (string? id) => { lastReportedIssue = id; request.ReportIssue(id); };

        bool issueDispatchedThisCycle = false;

        while (remaining > 0)
        {
            if (ct.IsCancellationRequested) break;

            var (hasIssues, hasPrs, hasDecomp) = ComputeQueueAvailability(request, activeDecompositionCount);
            var (foundTurn, selectedTurn) = TrySelectHighestPriorityQueue(hasIssues, hasPrs, hasDecomp);
            if (!foundTurn) break;

            var roundCtx = new RoundDispatchContext
            {
                PollableTemplates = request.PollableTemplates,
                ActiveIssueIdentifiers = request.ActiveIssueIdentifiers,
                TemplateProjectLookup = templateProjectLookup,
                TrackingReportIssue = trackingReportIssue,
                ReportStatus = request.ReportStatus,
                NotifyChange = request.NotifyChange,
                RemainingBudget = remaining,
                GetCurrentIssueIdentifier = () => lastReportedIssue
            };

            var turnResult = await ExecuteTurnAsync(
                selectedTurn, request, roundCtx, cycleStateCache,
                new TurnEligibility(hasIssues, hasPrs, hasDecomp, activeDecompositionCount),
                stoppingToken, ct);

            remaining -= turnResult.Consumed;
            processedCount += turnResult.Processed;
            failedCount += turnResult.Failed;
            activeDecompositionCount += turnResult.AdditionalDecomp;

            if (turnResult.IssueMadeProgress) issueDispatchedThisCycle = true;

            if (ct.IsCancellationRequested || remaining <= 0) break;
            if (!turnResult.AnyProgress) break;
        }

        // Floor pass: if Issues were present but never dispatched in the priority loop,
        // spend the reserved floor slots exclusively for Issues.
        var floorResult = await RunFloorPassAsync(
            request, floorEnabled, issueDispatchedThisCycle, issueFloor,
            totalBudget, processedCount, failedCount, activeDecompositionCount,
            templateProjectLookup, trackingReportIssue, cycleStateCache,
            stoppingToken, ct);
        remaining -= floorResult.Consumed;
        processedCount += floorResult.Processed;
        failedCount += floorResult.Failed;

        // Compute total budget remaining for telemetry (accounts for both priority loop and floor pass)
        // TODO: totalRemaining can be negative if processedCount+failedCount somehow exceeds totalBudget
        // (should not occur with the capped floorBudget fix, but EmitSkippedMaxRunsTelemetry gates on
        // remaining > 0, so a negative value would silently suppress the SkippedMaxRuns signal even when
        // items remain queued). Consider Math.Max(0, ...) here for defensive accuracy.
        int totalRemaining = totalBudget == int.MaxValue
            ? remaining
            : totalBudget - processedCount - failedCount;

        EmitSkippedMaxRunsTelemetry(request, totalRemaining);

        return new DispatchResult(processedCount, failedCount);
    }

    /// <summary>
    /// Runs the issue floor pass after the priority loop. If issues were present but never dispatched
    /// in the priority loop, dispatches up to <paramref name="issueFloor"/> additional issue items to
    /// ensure the minimum allocation guarantee is met.
    /// </summary>
    private async Task<(int Consumed, int Processed, int Failed)> RunFloorPassAsync(
        DispatchRoundRobinRequest request,
        bool floorEnabled,
        bool issueDispatchedThisCycle,
        int issueFloor,
        int totalBudget,
        int processedCount,
        int failedCount,
        int activeDecompositionCount,
        Dictionary<string, PipelineProject> templateProjectLookup,
        Action<string?> trackingReportIssue,
        Dictionary<int, bool> cycleStateCache,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        if (!floorEnabled || issueDispatchedThisCycle || ct.IsCancellationRequested)
            return (0, 0, 0);

        var (hasIssues, _, _) = ComputeQueueAvailability(request, activeDecompositionCount);
        if (!hasIssues) return (0, 0, 0);

        // Floor budget = reserved slots, capped to the actual remaining cycle budget.
        // For unlimited budgets: use issueFloor directly (no reservation was applied).
        // For finite budgets: cap at (totalBudget - slotsAlreadyUsed) so that the floor
        // pass can never cause total dispatches to exceed MaxRunsPerCycle. Using
        // `issueFloor + remaining` alone would overflow when MinIssueSlots >= MaxRunsPerCycle:
        // the Math.Max(..., 1) clamp forces priorityBudget=1, consuming 1 slot, leaving
        // remaining=0, then floorBudget=issueFloor+0=issueFloor resurrects slots that
        // were never legitimately available (e.g. MinIssueSlots=2, MaxRunsPerCycle=2
        // → priority dispatches 1, floor dispatches 2 → 3 total against a budget of 2).
        int floorBudget = totalBudget == int.MaxValue
            ? issueFloor
            : Math.Min(issueFloor, totalBudget - processedCount - failedCount);

        if (floorBudget <= 0) return (0, 0, 0);

        string? lastReportedIssue = null;
        var floorCtx = new RoundDispatchContext
        {
            PollableTemplates = request.PollableTemplates,
            ActiveIssueIdentifiers = request.ActiveIssueIdentifiers,
            TemplateProjectLookup = templateProjectLookup,
            TrackingReportIssue = id => { lastReportedIssue = id; trackingReportIssue(id); },
            ReportStatus = request.ReportStatus,
            NotifyChange = request.NotifyChange,
            RemainingBudget = floorBudget,
            GetCurrentIssueIdentifier = () => lastReportedIssue
        };
        var (_, count, p, f) = await DispatchIssueRoundAsync(
            floorCtx, request.IssueQueues, cycleStateCache, stoppingToken, ct);
        // TODO: [WARNING] In the unlimited-budget path, `remaining` in the caller is
        // `int.MaxValue - n` (still effectively int.MaxValue), so subtracting `count`
        // does not change the observable value. For telemetry, `totalRemaining` in the
        // unlimited-budget branch uses `remaining` directly, which is not meaningful
        // after the floor pass — it does not reflect floor-dispatched items. The
        // `remaining` variable is the wrong source of truth for unlimited-budget
        // telemetry; consider tracking floor-dispatched items separately.
        return (count, p, f);
    }

    private readonly record struct TurnResult(
        bool IssueMadeProgress, bool PrMadeProgress, bool DecompMadeProgress,
        int Consumed, int Processed, int Failed, int AdditionalDecomp)
    {
        public bool AnyProgress => IssueMadeProgress || PrMadeProgress || DecompMadeProgress;
    }

    /// <summary>
    /// Groups the queue eligibility flags for <see cref="ExecuteTurnAsync"/> to reduce its
    /// parameter count (S107).
    /// </summary>
    private readonly record struct TurnEligibility(
        bool HasIssues,
        bool HasPrs,
        bool HasDecomp,
        int ActiveDecompositionCount);

    /// <summary>
    /// Executes the dispatch logic for the selected turn (Issues, PullRequests, or Decomposition).
    /// Also handles the project-level decomposition fallback when the regular decomposition queue
    /// makes no progress.
    /// </summary>
    private async Task<TurnResult> ExecuteTurnAsync(
        DispatchTurn currentTurn,
        DispatchRoundRobinRequest request,
        RoundDispatchContext roundCtx,
        Dictionary<int, bool> cycleStateCache,
        TurnEligibility eligibility,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool issueMadeProgress = false, prMadeProgress = false, decompMadeProgress = false;
        int consumed = 0, processed = 0, failed = 0, additionalDecomp = 0;

        if (currentTurn == DispatchTurn.Issues && eligibility.HasIssues)
        {
            var (progress, count, p, f) = await DispatchIssueRoundAsync(
                roundCtx, request.IssueQueues, cycleStateCache, stoppingToken, ct);
            issueMadeProgress = progress; consumed += count; processed += p; failed += f;
        }

        if (currentTurn == DispatchTurn.PullRequests && eligibility.HasPrs)
        {
            var (progress, count, p, f) = await DispatchPrRoundAsync(
                roundCtx, request.PrQueues, stoppingToken, ct);
            prMadeProgress = progress; consumed += count; processed += p; failed += f;
        }

        if (currentTurn == DispatchTurn.Decomposition && eligibility.HasDecomp)
        {
            var (progress, count, p, f, addl) = await DispatchDecompositionRoundAsync(
                roundCtx, request.DecompositionQueues, request.Config, eligibility.ActiveDecompositionCount, stoppingToken, ct);
            decompMadeProgress = progress; consumed += count; processed += p; failed += f; additionalDecomp += addl;
        }

        // Project-level decomposition fallback — runs when regular decomposition made no progress
        bool canDispatchProjectLevel = currentTurn == DispatchTurn.Decomposition
            && !decompMadeProgress
            && request.ProjectLevelDecompositionQueues.Count > 0
            && (eligibility.ActiveDecompositionCount + additionalDecomp) < request.Config.MaxConcurrentDecompositions;

        if (canDispatchProjectLevel)
        {
            var (progress, count, p, f, addl) = await DispatchProjectLevelDecompositionRoundAsync(
                roundCtx, request.ProjectLevelDecompositionQueues, request.Config,
                eligibility.ActiveDecompositionCount + additionalDecomp, stoppingToken, ct);
            decompMadeProgress = progress; consumed += count; processed += p; failed += f; additionalDecomp += addl;
        }

        return new TurnResult(issueMadeProgress, prMadeProgress, decompMadeProgress,
            consumed, processed, failed, additionalDecomp);
    }

    private static void EmitSkippedMaxRunsTelemetry(DispatchRoundRobinRequest request, int remaining)
    {
        if (remaining > 0) return;
        var remainingItems = request.IssueQueues.Values.Sum(q => q.Count)
            + request.PrQueues.Values.Sum(q => q.Count)
            + request.DecompositionQueues.Values.Sum(q => q.Count)
            + request.ProjectLevelDecompositionQueues.Values.Sum(q => q.Count);
        if (remainingItems > 0)
            PipelineTelemetry.LoopDispatchDecisions.Add(remainingItems,
                new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedMaxRuns));
    }

    /// <summary>
    /// Computes whether each queue type has eligible work, respecting decomposition concurrency limits.
    /// </summary>
    private static (bool hasIssues, bool hasPrs, bool hasDecomp) ComputeQueueAvailability(
        DispatchRoundRobinRequest request, int activeDecompositionCount)
    {
        var hasIssues = HasEligible(request.PollableTemplates, request.IssueQueues, t => t.ImplementationEnabled);
        var hasPrs = HasEligible(request.PollableTemplates, request.PrQueues, t => t.ReviewEnabled);
        var hasDecomp = (HasEligible(request.PollableTemplates, request.DecompositionQueues, t => t.DecompositionEnabled)
            || HasEligibleProjectLevelDecomposition(request.ProjectLevelDecompositionQueues))
            && activeDecompositionCount < request.Config.MaxConcurrentDecompositions;
        return (hasIssues, hasPrs, hasDecomp);
    }

    /// <summary>
    /// Priority order for turn selection. Review (PRs) is dispatched first, then Decomposition,
    /// then Issues (Implementation). Within each type, FIFO order is preserved by the per-queue
    /// dequeue logic.
    /// </summary>
    private static readonly DispatchTurn[] PriorityOrder =
        [DispatchTurn.PullRequests, DispatchTurn.Decomposition, DispatchTurn.Issues];

    /// <summary>
    /// Selects the next eligible queue type using priority ordering: PullRequests first,
    /// then Decomposition, then Issues. Returns (found=false, default) when all queues are exhausted.
    /// </summary>
    internal static (bool found, DispatchTurn selectedTurn) TrySelectHighestPriorityQueue(
        bool hasIssues, bool hasPrs, bool hasDecomp)
    {
        foreach (var turn in PriorityOrder)
        {
            if ((turn == DispatchTurn.Issues && hasIssues)
                || (turn == DispatchTurn.PullRequests && hasPrs)
                || (turn == DispatchTurn.Decomposition && hasDecomp))
                return (true, turn);
        }
        return (false, default);
    }
}
