using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Pending job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingJob
{
    public required string IssueIdentifier { get; init; }
    public string? IssueTitle { get; init; }
    public required string IssueProviderId { get; init; }
    public required string RepoProviderId { get; init; }
    public string? BrainProviderId { get; init; }
    public string? PipelineProviderId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public required string InitiatedBy { get; init; }
    public IReadOnlyList<string> RequiredLabels { get; init; } = [];
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;
    public string? PrBranchName { get; init; }
    public string? PrDescription { get; init; }
    public string? PrUrl { get; init; }
    public string? PrTargetBranch { get; init; }
}

/// <summary>
/// Manages the job queue and agent selection for dispatching pipeline runs.
/// Uses a <see cref="ConcurrentQueue{T}"/> for FIFO job ordering and a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for duplicate issue detection.
/// Registered as a singleton in DI.
/// </summary>
public sealed class JobDispatcherService
{
    private readonly AgentRegistryService _registry;

    private readonly ConcurrentQueue<PendingJob> _jobQueue = new();

    private readonly ConcurrentDictionary<string, bool> _processingIssues = new();

    private readonly ILogger _logger;

    private readonly object _queueLock = new();

    public JobDispatcherService(AgentRegistryService registry, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Selects an idle agent whose labels are a superset of the required labels.
    /// When multiple agents match, selects the one idle longest (FIFO by
    /// <see cref="AgentEntry.LastJobCompletedAt"/>, falling back to <see cref="AgentEntry.RegisteredAt"/>).
    /// </summary>
    /// <returns>The best matching idle agent, or <c>null</c> if none available.</returns>
    public AgentEntry? SelectAgent(IReadOnlyList<string> requiredLabels)
    {
        ArgumentNullException.ThrowIfNull(requiredLabels);

        var idleAgents = _registry.GetIdleAgents();

        var compatible = idleAgents
            .Where(agent => !agent.Disabled)
            .Where(agent => LabelMatchHelper.IsLabelMatch(agent.Labels, requiredLabels))
            .OrderBy(agent => agent.LastJobCompletedAt ?? agent.RegisteredAt)
            .ToList();

        return compatible.Count > 0 ? compatible[0] : null;
    }

    /// <summary>
    /// Enqueues a job for later dispatch. Rejects duplicates (same issue identifier).
    /// </summary>
    /// <returns><c>true</c> if the job was enqueued; <c>false</c> if the issue is already queued or being processed.</returns>
    public bool EnqueueJob(PendingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_processingIssues.TryAdd(job.IssueIdentifier, true))
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
    /// Dequeues the next compatible job for the given agent.
    /// Scans the queue for the first job whose required labels match the agent's labels.
    /// Non-matching jobs are re-enqueued at the back.
    /// </summary>
    /// <returns>The next compatible job, or <c>null</c> if no compatible jobs are queued.</returns>
    public PendingJob? DequeueForAgent(AgentEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_queueLock)
        {
            var count = _jobQueue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_jobQueue.TryDequeue(out var job))
                    break;

                if (LabelMatchHelper.IsLabelMatch(agent.Labels, job.RequiredLabels))
                {
                    _logger.Information(
                        "Dequeued job for issue {IssueIdentifier} → agent {AgentId}",
                        job.IssueIdentifier, agent.AgentId);
                    return job;
                }

                // Not compatible — put it back
                _jobQueue.Enqueue(job);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether the given issue identifier is already queued for processing.
    /// </summary>
    public bool IsIssueQueued(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        return _processingIssues.ContainsKey(issueIdentifier);
    }

    /// <summary>
    /// Removes a queued issue (e.g., when the UI cancels a pending job).
    /// Removes from both the dedup dictionary and the queue.
    /// </summary>
    public bool RemoveFromQueue(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        if (!_processingIssues.TryRemove(issueIdentifier, out _))
            return false;

        lock (_queueLock)
        {
            var count = _jobQueue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_jobQueue.TryDequeue(out var job))
                    break;

                if (job.IssueIdentifier != issueIdentifier)
                {
                    _jobQueue.Enqueue(job);
                }
            }
        }

        _logger.Information("Removed queued job for issue {IssueIdentifier}", issueIdentifier);
        return true;
    }

    /// <summary>
    /// Marks an issue as no longer being processed (call after job completion or failure).
    /// </summary>
    public void MarkIssueComplete(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        _processingIssues.TryRemove(issueIdentifier, out _);
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
    /// repo <c>requiredAgentLabels</c> setting →
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
