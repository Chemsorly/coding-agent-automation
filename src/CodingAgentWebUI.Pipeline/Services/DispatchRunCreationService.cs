using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service responsible for creating and registering dispatched pipeline runs.
/// Implements <see cref="IDispatchRunCreator"/> — extracted from <see cref="PipelineOrchestrationService"/>
/// to reduce its responsibility surface. Handles provider resolution, run construction,
/// and dedup-guard registration via <see cref="PipelineRunLifecycleService"/>.
/// </summary>
public class DispatchRunCreationService : IDispatchRunCreator, IAsyncDisposable, IDisposable
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly PipelineProviderManager _providerManager;
    private readonly IProviderFactory _providerFactory;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Atomic in-flight reservation set. Prevents the TOCTOU race between
    /// <see cref="PipelineRunLifecycleService.IsIssueBeingProcessed"/> and
    /// <see cref="PipelineRunLifecycleService.RegisterDispatchedRun"/> by ensuring only one
    /// concurrent caller can proceed through the async provider resolution gap for a given issue.
    /// Key format: "{issueProviderConfigId}:{issueIdentifier}"
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _dispatchingIssues = new();

    public DispatchRunCreationService(
        PipelineRunLifecycleService lifecycle,
        IProviderConfigStore configStore,
        IProviderFactory providerFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _lifecycle = lifecycle;
        _providerManager = new PipelineProviderManager(configStore, providerFactory, logger);
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetAllActiveRuns() => _lifecycle.GetAllActiveRuns();

    /// <inheritdoc />
    public bool IsIssueBeingProcessed(string issueIdentifier, ProviderConfigId issueProviderConfigId) =>
        _lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId.Value);

    /// <inheritdoc />
    public async Task<PipelineRun?> CreateDispatchedRunAsync(DispatchRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.IssueIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(request.IssueProviderId.Value, nameof(request.IssueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(request.RepoProviderId.Value, nameof(request.RepoProviderId));
        ArgumentException.ThrowIfNullOrEmpty(request.AgentProviderId.Value, nameof(request.AgentProviderId));

        var issueProviderId = request.IssueProviderId;
        var issueIdentifier = request.IssueIdentifier;

        var compositeKey = $"{issueProviderId.Value}:{issueIdentifier}";

        // Atomic reservation — TryAdd fails if another thread is already dispatching this issue
        if (!_dispatchingIssues.TryAdd(compositeKey, 0))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being dispatched by another caller, skipping", issueIdentifier);
            return null;
        }

        try
        {
            if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
            {
                _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping dispatch", issueIdentifier);
                return null;
            }

            var run = await ResolveAndCreateRunAsync(request, ct);

            if (!_lifecycle.RegisterDispatchedRun(run))
                return null;

            _logger.Information(
                "Dispatched run {RunId} created for issue {IssueIdentifier} → agent {AgentId}",
                run.RunId, issueIdentifier, request.AgentId);

            return run;
        }
        finally
        {
            // TODO: Add a unit test that verifies reservation release on failure paths (e.g.,
            // RegisterDispatchedRun returns false or ResolveAndCreateRunAsync throws). If this
            // TryRemove is accidentally moved or an early return is added before the try block,
            // the reservation would leak and permanently block dispatch for this issue.
            _dispatchingIssues.TryRemove(compositeKey, out _);
        }
    }

    /// <inheritdoc />
    public async Task<RunReservation?> ReserveRunIdAsync(DispatchRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.IssueIdentifier);
        ArgumentException.ThrowIfNullOrEmpty(request.IssueProviderId.Value, nameof(request.IssueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(request.RepoProviderId.Value, nameof(request.RepoProviderId));
        ArgumentException.ThrowIfNullOrEmpty(request.AgentProviderId.Value, nameof(request.AgentProviderId));

        var issueProviderId = request.IssueProviderId;
        var issueIdentifier = request.IssueIdentifier;

        var compositeKey = $"{issueProviderId.Value}:{issueIdentifier}";

        // Atomic reservation — TryAdd fails if another thread is already dispatching this issue
        if (!_dispatchingIssues.TryAdd(compositeKey, 0))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being dispatched by another caller, skipping reservation", issueIdentifier);
            return null;
        }

        try
        {
            if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
            {
                _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping reservation", issueIdentifier);
                return null;
            }

            // TODO: startedAt is captured before ResolveAndCreateRunAsync (provider resolution).
            // Original code captured it after provider resolution. If excluding provider resolution
            // latency from start time matters, move this assignment after the helper call.
            var startedAt = DateTimeOffset.UtcNow;

            var sentinel = await ResolveAndCreateRunAsync(request with { RunType = PipelineRunType.Implementation }, ct);

            if (!_lifecycle.RegisterDispatchedRun(sentinel))
                return null;

            _logger.Information(
                "Reserved run {RunId} for issue {IssueIdentifier}",
                sentinel.RunId, issueIdentifier);

            return new RunReservation(sentinel.RunId, sentinel.RepositoryName!, sentinel.ModelName!, startedAt);
        }
        finally
        {
            // TODO: Add a unit test that verifies reservation release on failure paths (e.g.,
            // RegisterDispatchedRun returns false or ResolveAndCreateRunAsync throws). If this
            // TryRemove is accidentally moved or an early return is added before the try block,
            // the reservation would leak and permanently block reservation for this issue.
            _dispatchingIssues.TryRemove(compositeKey, out _);
        }
    }

    /// <inheritdoc />
    public void RegisterDispatchedRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _lifecycle.ReplaceDispatchedRun(run);
    }

    /// <summary>
    /// Resolves provider configs and creates a fully-constructed <see cref="PipelineRun"/> with
    /// metadata (RepositoryName, ModelName, PipelineProviderConfigId) already set.
    /// Shared by <see cref="CreateDispatchedRunAsync"/> and <see cref="ReserveRunIdAsync"/>.
    /// </summary>
    private async Task<PipelineRun> ResolveAndCreateRunAsync(
        DispatchRunRequest request,
        CancellationToken ct)
    {
        var repoProviderId = request.RepoProviderId;
        var agentProviderId = request.AgentProviderId;
        var issueIdentifier = request.IssueIdentifier;
        var issueProviderId = request.IssueProviderId;
        var agentId = request.AgentId;
        var brainProviderId = request.BrainProviderId;
        var pipelineProviderId = request.PipelineProviderId;
        var initiatedBy = request.InitiatedBy;
        var runType = request.RunType;

        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId.Value, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

        var run = runType switch
        {
            PipelineRunType.Review => PipelineRun.CreateReview(
                runId: Guid.NewGuid().ToString(),
                issueIdentifier: issueIdentifier,
                issueTitle: string.Empty,
                issueProviderConfigId: issueProviderId.Value,
                repoProviderConfigId: repoProviderId.Value,
                reviewPrBranchName: string.Empty,
                reviewPrTargetBranch: string.Empty,
                initiatedBy: initiatedBy,
                agentId: agentId,
                agentProviderConfigId: agentProviderId.Value,
                brainProviderConfigId: brainProviderId),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => PipelineRun.CreateDecomposition(
                runId: Guid.NewGuid().ToString(),
                issueIdentifier: issueIdentifier,
                issueTitle: string.Empty,
                issueProviderConfigId: issueProviderId.Value,
                repoProviderConfigId: repoProviderId.Value,
                phaseType: runType,
                initiatedBy: initiatedBy,
                agentId: agentId,
                agentProviderConfigId: agentProviderId.Value,
                brainProviderConfigId: brainProviderId),
            _ => PipelineRun.CreateImplementation(
                runId: Guid.NewGuid().ToString(),
                issueIdentifier: issueIdentifier,
                issueTitle: string.Empty,
                issueProviderConfigId: issueProviderId.Value,
                repoProviderConfigId: repoProviderId.Value,
                initiatedBy: initiatedBy,
                agentId: agentId,
                agentProviderConfigId: agentProviderId.Value,
                brainProviderConfigId: brainProviderId)
        };
        run.RepositoryName = tempRepoProvider.RepositoryFullName;
        run.ModelName = configuredModel;
        run.PipelineProviderConfigId = pipelineProviderId;

        return run;
    }

    // TODO: Add a `bool _disposed` guard to make DisposeAsync idempotent. Currently, double-calling
    // DisposeAsync delegates to _providerManager.DisposeAsync() twice, which attempts to dispose
    // already-disposed provider instances (tolerated but violates the idempotency contract).
    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // Sync-only managed resources: none currently (provider manager is async-disposed).
            // DisposeAsync() handles _providerManager disposal.
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        // Do not call DisposeAsync synchronously — .GetAwaiter().GetResult()
        // deadlocks in Blazor Server's SynchronizationContext.
        // DisposeAsync() is the correct disposal path; sync Dispose handles only sync resources.
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _providerManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
