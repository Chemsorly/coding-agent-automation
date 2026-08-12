using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Manages the job queue and agent selection for dispatching pipeline runs.
/// Uses a <see cref="ConcurrentQueue{T}"/> for FIFO job ordering and a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for duplicate issue detection.
/// Registered as a singleton in DI.
/// </summary>
public sealed class JobDeduplicationGuardService : IJobDeduplicationGuard
{
    private readonly IAgentRegistryService _registry;

    private readonly ConcurrentQueue<PendingJob> _jobQueue = new();

    private readonly ConcurrentDictionary<string, bool> _processingIssues = new();

    private readonly ILogger _logger;

    /// <summary>Guards compound queue operations (scan-and-re-enqueue). See docs/architecture/concurrency-model.md</summary>
    private readonly object _queueLock = new();

    /// <summary>Serializes agent selection to prevent double-booking. See docs/architecture/concurrency-model.md</summary>
    private readonly object _selectionLock = new();

    public JobDeduplicationGuardService(IAgentRegistryService registry, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Selects an idle agent whose labels are a superset of the required labels and
    /// atomically reserves it by transitioning to <see cref="AgentStatus.Busy"/>.
    /// This prevents concurrent dispatch paths from selecting the same agent.
    /// When multiple agents match, selects the one idle longest (FIFO by
    /// <see cref="AgentEntry.LastJobCompletedAt"/>, falling back to <see cref="AgentEntry.RegisteredAt"/>).
    /// </summary>
    /// <returns>The reserved agent (already transitioned to Busy), or <c>null</c> if none available.</returns>
    public AgentEntry? SelectAgent(IReadOnlyList<string> requiredLabels)
    {
        ArgumentNullException.ThrowIfNull(requiredLabels);

        lock (_selectionLock)
        {
            var idleAgents = _registry.GetIdleAgents();

            if (idleAgents.Count == 0)
            {
                _logger.Debug("SelectAgent: no idle agents available (requiredLabels=[{Labels}])",
                    string.Join(", ", requiredLabels));
                return null;
            }

            var compatible = idleAgents
                .Where(agent => !agent.Disabled)
                .Where(agent => LabelMatchHelper.IsLabelMatch(agent.Labels, requiredLabels))
                .OrderBy(agent => agent.LastJobCompletedAt ?? agent.RegisteredAt)
                .ToList();

            if (compatible.Count == 0)
            {
                _logger.Debug("SelectAgent: {IdleCount} idle agent(s) but none match requiredLabels=[{Labels}]",
                    idleAgents.Count, string.Join(", ", requiredLabels));
                return null;
            }

            // Iterate compatible agents with double-check pattern:
            // Lock the entry and verify status is still Idle before transitioning.
            // HeartbeatMonitor may have marked the agent Disconnected between GetIdleAgents() and now.
            // Lock ordering: _selectionLock (already held) → entry.SyncRoot (no deadlock risk).
            foreach (var candidate in compatible)
            {
                lock (candidate.SyncRoot)
                {
                    if (candidate.Status != AgentStatus.Idle)
                    {
                        // Race: HeartbeatMonitor changed status between snapshot and lock acquisition — skip
                        _logger.Debug("SelectAgent: skipping agent {AgentId} — status changed to {Status} before reservation",
                            candidate.AgentId, candidate.Status);
                        continue;
                    }

                    // Atomically reserve the agent so no other dispatch path can select it
                    candidate.Status = AgentStatus.Busy;
                    candidate.BusySince = DateTimeOffset.UtcNow;
                }

                _logger.Debug("SelectAgent: reserved agent {AgentId} for requiredLabels=[{Labels}] ({CompatibleCount} compatible, {IdleCount} idle)",
                    candidate.AgentId, string.Join(", ", requiredLabels), compatible.Count, idleAgents.Count);

                return candidate;
            }

            _logger.Debug("SelectAgent: all {CompatibleCount} compatible agents had status change before reservation (requiredLabels=[{Labels}])",
                compatible.Count, string.Join(", ", requiredLabels));
            return null;
        }
    }

    /// <summary>
    /// Enqueues a job for later dispatch. Rejects duplicates (same issue identifier).
    /// </summary>
    /// <returns><c>true</c> if the job was enqueued; <c>false</c> if the issue is already queued or being processed.</returns>
    public bool EnqueueJob(PendingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var compositeKey = $"{job.IssueProviderId}:{job.IssueIdentifier}";
        if (!_processingIssues.TryAdd(compositeKey, true))
        {
            _logger.Warning(
                "Job for issue {IssueIdentifier} rejected — already queued or processing",
                job.IssueIdentifier);
            return false;
        }

        _jobQueue.Enqueue(job);
        _logger.Information(
            "Job enqueued for issue {IssueIdentifier} (initiated by {InitiatedBy})",
            job.IssueIdentifier, job.InitiatedBy);
        return true;
    }

    /// <summary>
    /// Dequeues the highest-priority compatible job for the given agent.
    /// Collects all jobs under <c>_queueLock</c>, selects the compatible job with the lowest
    /// priority number (Review=0 &gt; Decomposition=1 &gt; Implementation=2 &gt; Consolidation=3),
    /// breaking ties by <see cref="PendingJob.EnqueuedAt"/> to preserve FIFO within the same tier.
    /// Non-selected jobs are re-enqueued in their original order to preserve within-tier FIFO for
    /// subsequent calls.
    /// </summary>
    /// <returns>The highest-priority compatible job, or <c>null</c> if no compatible jobs are queued.</returns>
    public PendingJob? DequeueForAgent(AgentEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_queueLock)
        {
            var count = _jobQueue.Count;
            if (count == 0) return null;

            // Step 1: Collect all jobs in FIFO order
            // TODO: Using _jobQueue.Count as a snapshot before the drain loop means items
            // concurrently enqueued (by EnqueueJob or ReEnqueue outside this lock) between the
            // Count read and the loop completing will not be included in `all`. Those items stay
            // at the back of ConcurrentQueue and are considered on the next call — no job is lost,
            // but a high-priority job enqueued during this window will not be promoted in the
            // current cycle. A tighter implementation would drain until TryDequeue returns false
            // rather than relying on a Count snapshot.
            var all = new List<PendingJob>(count);
            for (var i = 0; i < count; i++)
            {
                if (_jobQueue.TryDequeue(out var j))
                    all.Add(j);
            }

            // Step 2: Find the highest-priority label-compatible job
            PendingJob? selected = null;
            int selectedIndex = -1;
            int bestPriority = int.MaxValue;
            DateTimeOffset bestEnqueuedAt = DateTimeOffset.MaxValue;

            for (var i = 0; i < all.Count; i++)
            {
                var job = all[i];
                if (!LabelMatchHelper.IsLabelMatch(agent.Labels, job.RequiredLabels))
                    continue;

                var p = GetJobPriority(job);
                if (p < bestPriority || (p == bestPriority && job.EnqueuedAt < bestEnqueuedAt))
                {
                    bestPriority = p;
                    bestEnqueuedAt = job.EnqueuedAt;
                    selected = job;
                    selectedIndex = i;
                }
            }

            // Step 3: Re-enqueue all non-selected jobs in their original collected order
            // (not sorted by priority) to preserve within-tier FIFO for subsequent calls.
            for (var i = 0; i < all.Count; i++)
            {
                if (i != selectedIndex)
                    _jobQueue.Enqueue(all[i]);
            }

            if (selected is not null)
                _logger.Information(
                    "Dequeued job for issue {IssueIdentifier} → agent {AgentId}",
                    selected.IssueIdentifier, agent.AgentId);

            return selected;
        }
    }

    /// <summary>
    /// Returns the dispatch priority for a job. Lower values are dispatched first.
    /// Priority order: Review (0) &gt; Decomposition (1) &gt; Implementation (2) &gt; Consolidation (3).
    /// Consolidation is detected via <see cref="PendingJob.IsConsolidation"/> because
    /// <see cref="PipelineRunType"/> has no Consolidation value — consolidation jobs have
    /// <see cref="PipelineRunType.Implementation"/> as their RunType.
    /// The default arm (<c>_ =&gt; 2</c>) is unreachable with the current enum but acts as a
    /// safe fallback for any future values.
    /// </summary>
    private static int GetJobPriority(PendingJob job) => job.IsConsolidation
        ? 3
        : job.RunType switch
        {
            PipelineRunType.Review                => 0,
            PipelineRunType.DecompositionAnalysis => 1,
            PipelineRunType.Decomposition         => 1,
            PipelineRunType.Implementation        => 2,
            _                                     => 2
        };

    /// <summary>
    /// Checks whether the given issue identifier is already queued for processing.
    /// </summary>
    public bool IsIssueQueued(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        // TODO: [WARNING] ThrowIfNullOrEmpty(issueIdentifier.Value) reports the parameter name as
        // "issueIdentifier.Value" (via CallerArgumentExpression) rather than "issueIdentifier".
        // Pass the explicit name to improve debuggability:
        // ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentException.ThrowIfNullOrEmpty(issueProviderConfigId.Value);
        var compositeKey = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        return _processingIssues.ContainsKey(compositeKey);
    }

    /// <summary>
    /// Checks whether the given issue identifier is queued with any provider.
    /// Intended for test convenience where provider context is implicit.
    /// WARNING: O(n) enumeration of all keys — do not use on hot paths.
    /// </summary>
    internal bool IsIssueQueued(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        var suffix = $":{issueIdentifier}";
        return _processingIssues.Keys.Any(k => k.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Removes a queued issue (e.g., when the UI cancels a pending job).
    /// Removes from both the dedup dictionary and the queue.
    /// </summary>
    public bool RemoveFromQueue(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        // TODO: [WARNING] ThrowIfNullOrEmpty(issueIdentifier.Value) reports the parameter name as
        // "issueIdentifier.Value" (via CallerArgumentExpression) rather than "issueIdentifier".
        // Pass the explicit name to improve debuggability:
        // ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentException.ThrowIfNullOrEmpty(issueProviderConfigId.Value);

        var compositeKey = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        if (!_processingIssues.TryRemove(compositeKey, out _))
            return false;

        lock (_queueLock)
        {
            var count = _jobQueue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_jobQueue.TryDequeue(out var job))
                    break;

                if (!(job.IssueIdentifier == issueIdentifier && job.IssueProviderId == issueProviderConfigId.Value))
                {
                    _jobQueue.Enqueue(job);
                }
            }
        }

        _logger.Information("Removed queued job for issue {IssueIdentifier}", issueIdentifier);
        return true;
    }

    /// <summary>
    /// Removes a queued job by its RunId (IssueIdentifier for consolidation jobs).
    /// Used when a consolidation run is cancelled while queued.
    /// </summary>
    public bool RemoveJob(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return RemoveFromQueue(runId, "consolidation");
    }

    /// <summary>
    /// Marks an issue as no longer being processed (call after job completion or failure).
    /// </summary>
    public void MarkIssueComplete(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        // TODO: [WARNING] ThrowIfNullOrEmpty(issueIdentifier.Value) reports the parameter name as
        // "issueIdentifier.Value" (via CallerArgumentExpression) rather than "issueIdentifier".
        // Pass the explicit name to improve debuggability:
        // ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentException.ThrowIfNullOrEmpty(issueProviderConfigId.Value);
        var compositeKey = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        _processingIssues.TryRemove(compositeKey, out _);
    }

    /// <summary>
    /// Re-enqueues a job that was previously dequeued but could not be dispatched.
    /// Bypasses the <c>_processingIssues.TryAdd</c> check because the entry is still
    /// active in the dedup dictionary (caller guarantees this).
    /// </summary>
    public void ReEnqueue(PendingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobQueue.Enqueue(job);
        _logger.Debug(
            "Re-enqueued job for issue {IssueIdentifier} (dedup entry retained)",
            job.IssueIdentifier);
    }

    /// <summary>
    /// Returns the current number of jobs in the queue.
    /// </summary>
    public int QueueLength => _jobQueue.Count;

    /// <summary>
    /// Returns all currently queued jobs as a snapshot.
    /// </summary>
    public IReadOnlyList<PendingJob> GetQueuedJobs()
    {
        return _jobQueue.ToArray().ToList().AsReadOnly();
    }

    /// <summary>
    /// Resolves the required agent labels for a repository provider config.
    /// Delegates to <see cref="Pipeline.Services.LabelResolver.ResolveRequiredLabels"/> for the actual logic.
    /// Resolution order: <see cref="ProviderConfig.RequiredLabels"/> property →
    /// <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> → empty (any agent).
    /// </summary>
    public static IReadOnlyList<string> ResolveRequiredLabels(
        ProviderConfig? repoConfig,
        PipelineConfiguration pipelineConfig)
    {
        return Pipeline.Services.LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);
    }

    /// <summary>
    /// Clears the job queue and processing tracker. Used by E2E tests for state isolation.
    /// </summary>
    internal void Reset()
    {
        lock (_queueLock)
        {
            while (_jobQueue.TryDequeue(out _)) { }
        }
        _processingIssues.Clear();
    }
}
