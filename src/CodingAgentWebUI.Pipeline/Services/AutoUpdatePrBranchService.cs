using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Evaluates agent:done PRs, evicts resolved in-flight entries, and triggers
/// server-side branch updates for PRs that are behind base within the concurrency budget.
/// </summary>
/// <remarks>
/// State: <c>_inFlight</c> is a <c>ConcurrentDictionary&lt;string, HashSet&lt;int&gt;&gt;</c>
/// keyed by <c>repoProviderId</c>. The inner <c>HashSet&lt;int&gt;</c> is NOT thread-safe,
/// but is safe here because <see cref="ExecuteAsync"/> is called sequentially from the
/// poll tick (one call at a time per template). <see cref="UpdateAsync"/> does NOT
/// access <c>_inFlight</c> — it only calls the provider and emits telemetry.
/// If multi-template parallelism is ever introduced, replace the inner HashSet with
/// <c>ConcurrentDictionary&lt;int, byte&gt;</c>.
/// </remarks>
public sealed class AutoUpdatePrBranchService : IAutoUpdatePrBranchService
{
    private readonly IOrchestratorRunService _runService;
    private readonly ILogger _logger;

    /// <summary>
    /// In-flight PR numbers per repository, persisted across poll ticks.
    /// The slot represents the CI run lifetime, not the HTTP call lifetime.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<int>> _inFlight = new();

    public AutoUpdatePrBranchService(IOrchestratorRunService runService, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(logger);
        _runService = runService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        int effectiveConcurrencyLimit,
        CancellationToken ct)
    {
        var limit = Math.Max(1, effectiveConcurrencyLimit);
        var repoTag = new KeyValuePair<string, object?>("repo_provider_id", repoProviderId);

        // ── Step 1: Build mergeability map — one call per PR, reused below ──
        // N+1 trade-off: pool is small (bounded by unmerged agent PRs). See spec 040, Req 7.1.
        var mergeabilityMap = new Dictionary<int, bool?>(agentDonePrs.Count);
        foreach (var pr in agentDonePrs)
        {
            mergeabilityMap[pr.Number] = await repoProvider.IsPullRequestBehindBaseAsync(pr.Number, ct);
        }

        // ── Step 2: Get or create in-flight set ──────────────────────────────
        var inFlight = _inFlight.GetOrAdd(repoProviderId, _ => new HashSet<int>());

        // ── Step 3: Evict resolved in-flight entries ──────────────────────────
        var currentPrNumbers = new HashSet<int>(agentDonePrs.Select(p => p.Number));
        foreach (var prNumber in inFlight.ToList()) // snapshot for safe iteration
        {
            if (!currentPrNumbers.Contains(prNumber))
            {
                // PR merged or agent:done label removed — free slot
                inFlight.Remove(prNumber);
                PipelineTelemetry.AutoUpdateEvicted.Add(1, repoTag);
            }
            else if (mergeabilityMap[prNumber] != null)
            {
                // CI done (null = still running; non-null = resolved) — free slot.
                // IMPORTANT: do NOT add a 'continue' here for the result == true case.
                // A PR evicted with result = true (base moved again) is naturally
                // re-selected as a candidate in step 6 because inFlight.Contains
                // is now false. No special handling needed.
                inFlight.Remove(prNumber);
                PipelineTelemetry.AutoUpdateEvicted.Add(1, repoTag);
            }
            // else: null → CI still running → keep in set
        }

        // ── Step 4: Get active run branches (for rework exclusion) ───────────
        HashSet<string> activeRunBranches;
        try
        {
            activeRunBranches = _runService.GetActiveRuns()
                .Where(r => r.BranchName != null)
                .Select(r => r.BranchName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "AutoUpdatePrBranchService: failed to get active runs for branch exclusion; proceeding without exclusion");
            activeRunBranches = [];
        }

        // ── Step 5: Sort candidates ascending by PR number (oldest first) ────
        var sorted = agentDonePrs.OrderBy(p => p.Number).ToList();

        // ── Step 6: Select and trigger eligible updates ───────────────────────
        foreach (var pr in sorted)
        {
            if (inFlight.Count >= limit)
                break; // budget exhausted

            if (pr.IsDraft)
            {
                PipelineTelemetry.AutoUpdateSkipped.Add(1, repoTag);
                continue;
            }

            if (activeRunBranches.Contains(pr.BranchName))
            {
                PipelineTelemetry.AutoUpdateSkipped.Add(1, repoTag);
                continue;
            }

            if (inFlight.Contains(pr.Number))
            {
                PipelineTelemetry.AutoUpdateSkipped.Add(1, repoTag);
                continue;
            }

            var mergeability = mergeabilityMap[pr.Number]; // reuse from step 1
            if (mergeability == null)
            {
                // null = CI running or computing — skip this tick, re-evaluate next
                PipelineTelemetry.AutoUpdateSkipped.Add(1, repoTag);
                continue;
            }

            if (mergeability == false)
            {
                PipelineTelemetry.AutoUpdateSkipped.Add(1, repoTag);
                continue;
            }

            // mergeability == true → add to in-flight and fire update
            inFlight.Add(pr.Number);
            PipelineTelemetry.AutoUpdateTriggered.Add(1, repoTag);
            _ = UpdateAsync(repoProvider, repoProviderId, pr.Number, repoTag);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper for <see cref="IRepositoryProvider.UpdatePullRequestBranchAsync"/>.
    /// Uses <see cref="CancellationToken.None"/> so the HTTP call completes independently of
    /// the poll tick's cancellation token. The server-side effect proceeds regardless of shutdown.
    /// The PR stays in the in-flight set until the next eviction pass resolves it — the slot
    /// represents the CI run, not this HTTP call.
    /// </summary>
    private async Task UpdateAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        int prNumber,
        KeyValuePair<string, object?> repoTag)
    {
        try
        {
            await repoProvider.UpdatePullRequestBranchAsync(prNumber, CancellationToken.None);
            PipelineTelemetry.AutoUpdateSucceeded.Add(1, repoTag);
            _logger.Information(
                "Auto-updated branch for PR #{PrNumber} on repo {RepoProviderId}",
                prNumber, repoProviderId);
        }
        catch (Exception ex)
        {
            PipelineTelemetry.AutoUpdateFailed.Add(1, repoTag);
            _logger.Warning(ex,
                "Failed to auto-update branch for PR #{PrNumber} on {RepoProviderId}: {Error}",
                prNumber, repoProviderId, ex.Message);
            // PR stays in inFlight — eviction pass frees the slot next tick when mergeability resolves.
        }
    }
}
