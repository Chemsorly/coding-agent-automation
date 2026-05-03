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
    public required string IssueProviderId { get; init; }
    public required string RepoProviderId { get; init; }
    public string? BrainProviderId { get; init; }
    public string? PipelineProviderId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public required string InitiatedBy { get; init; }
    public IReadOnlyList<string> RequiredLabels { get; init; } = [];
}

/// <summary>
/// Manages the job queue and agent selection for dispatching pipeline runs.
/// Uses a <see cref="ConcurrentQueue{T}"/> for FIFO job ordering and a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for duplicate issue detection.
/// Registered as a singleton in DI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design Decision: Intentionally non-sealed.</b>
/// This class is non-sealed to allow E2E test subclasses (specifically
/// <c>ResettableJobDispatcherService</c> in
/// <c>tests/CodingAgentWebUI.E2ETests/Infrastructure/ResettableServices.cs</c>)
/// to inherit and expose a <c>Reset()</c> method for test isolation.
/// </para>
/// <para>
/// <b>Sealed + Composition vs Non-Sealed + Inheritance Tradeoff:</b>
/// The preferred .NET pattern is to seal classes by default and use composition-based
/// test doubles (e.g., wrapper/decorator pattern with extracted interfaces). The current
/// non-sealed + inheritance approach was chosen for pragmatic E2E test state reset without
/// polluting the production API with reset methods. Migration to sealed + composition
/// requires: (1) extracting an interface (e.g., <c>IJobDispatcherService</c>),
/// (2) updating E2E tests to use a wrapper/decorator that delegates to the real service
/// and adds reset capability, and (3) verifying no production code relies on inheritance.
/// This migration is documented as a future improvement — see Requirement 22.
/// </para>
/// </remarks>
public class JobDispatcherService
{
    private readonly AgentRegistryService _registry;

    /// <summary>
    /// FIFO queue of pending jobs awaiting dispatch. Exposed as <c>protected</c> to allow
    /// E2E test subclasses (e.g., <c>ResettableJobDispatcherService</c>) to drain the queue
    /// between tests.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly ConcurrentQueue<PendingJob> _jobQueue = new();

    /// <summary>
    /// Tracks issue identifiers currently queued or being processed for duplicate detection.
    /// Exposed as <c>protected</c> to allow E2E test subclasses to clear state between tests.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly ConcurrentDictionary<string, bool> _processingIssues = new();

    private readonly ILogger _logger;

    /// <summary>
    /// Synchronization object for queue operations that require atomicity (dequeue-and-requeue).
    /// Exposed as <c>protected</c> to allow E2E test subclasses to safely drain the queue
    /// under the same lock.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly object _queueLock = new();

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
            .Where(agent => IsLabelMatch(agent.Labels, requiredLabels))
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

                if (IsLabelMatch(agent.Labels, job.RequiredLabels))
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
    /// Checks whether the agent's labels are a superset of the required labels.
    /// An empty required labels list matches any agent.
    /// </summary>
    private static bool IsLabelMatch(IReadOnlyList<string> agentLabels, IReadOnlyList<string> requiredLabels)
    {
        if (requiredLabels.Count == 0)
            return true;

        var agentLabelSet = new HashSet<string>(agentLabels, StringComparer.OrdinalIgnoreCase);
        return requiredLabels.All(label => agentLabelSet.Contains(label));
    }
}
