using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Manages the queue of pending consolidation jobs awaiting dispatch.
/// Separate from <see cref="JobDispatcherService"/> because consolidation jobs have
/// different fields and concurrency semantics (keyed by RunId, not issue identifier).
/// </summary>
public sealed class ConsolidationQueueService
{
    private readonly ConcurrentQueue<PendingConsolidationJob> _queue = new();
    private readonly ConcurrentDictionary<string, bool> _queuedRunIds = new();
    // TODO: _cancelledRunIds grows unboundedly — entries are never removed after a cancelled job is fully processed. Add periodic cleanup or TTL-based eviction.
    private readonly ConcurrentDictionary<string, bool> _cancelledRunIds = new();
    private readonly ILogger _logger;

    public ConsolidationQueueService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Current number of jobs in the queue.</summary>
    public int QueueLength => _queue.Count;

    /// <summary>Enqueues a consolidation job for later dispatch.</summary>
    public void EnqueueJob(PendingConsolidationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_queuedRunIds.TryAdd(job.RunId, true))
        {
            _logger.Warning("Consolidation job {RunId} already queued, skipping", job.RunId);
            return;
        }

        _queue.Enqueue(job);
        _logger.Information("Consolidation job {RunId} enqueued (type={Type})", job.RunId, job.Type);
    }

    /// <summary>
    /// Dequeues the next compatible job for the given agent (label matching).
    /// Non-matching jobs are re-enqueued at the back.
    /// </summary>
    public PendingConsolidationJob? DequeueForAgent(AgentEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var count = _queue.Count;
        for (var i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var job))
                break;

            if (IsLabelMatch(agent.Labels, job.RequiredLabels))
            {
                _queuedRunIds.TryRemove(job.RunId, out _);
                return job;
            }

            _queue.Enqueue(job);
        }

        return null;
    }

    /// <summary>Checks whether a run is currently queued.</summary>
    public bool IsRunQueued(string runId) => _queuedRunIds.ContainsKey(runId);

    /// <summary>Marks a run as cancelled so it won't be dispatched during drain.</summary>
    public void MarkCancelled(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _cancelledRunIds.TryAdd(runId, true);
        RemoveFromQueue(runId);
    }

    /// <summary>Checks whether a run was cancelled (cancel-during-dispatch race guard).</summary>
    public bool IsCancelled(string runId) => _cancelledRunIds.ContainsKey(runId);

    /// <summary>Removes a job from the queue by RunId.</summary>
    public void RemoveFromQueue(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!_queuedRunIds.TryRemove(runId, out _))
            return;

        var count = _queue.Count;
        for (var i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var job))
                break;

            if (job.RunId != runId)
                _queue.Enqueue(job);
        }
    }

    private static bool IsLabelMatch(IReadOnlyList<string> agentLabels, IReadOnlyList<string> requiredLabels)
    {
        if (requiredLabels.Count == 0)
            return true;

        var agentLabelSet = new HashSet<string>(agentLabels, StringComparer.OrdinalIgnoreCase);
        return requiredLabels.All(label => agentLabelSet.Contains(label));
    }
}
