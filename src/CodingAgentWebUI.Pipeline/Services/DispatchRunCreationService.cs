using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service responsible for creating and registering dispatched pipeline runs.
/// Implements <see cref="IDispatchRunCreator"/> — extracted from <see cref="PipelineOrchestrationService"/>
/// to reduce its responsibility surface. Handles provider resolution, run construction,
/// and dedup-guard registration via <see cref="PipelineRunLifecycleService"/>.
/// </summary>
// TODO: Implement IAsyncDisposable — this class owns a PipelineProviderManager (IAsyncDisposable) via
// _providerManager but never disposes it. Currently safe because ResolveProviderConfigAsync doesn't
// populate Active* providers and temp providers are disposed inline, but the ownership contract is violated.
public class DispatchRunCreationService : IDispatchRunCreator
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly PipelineProviderManager _providerManager;
    private readonly IProviderFactory _providerFactory;
    private readonly Serilog.ILogger _logger;

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
    public async Task<PipelineRun?> CreateDispatchedRunAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, string issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch",
        PipelineRunType runType = PipelineRunType.Implementation)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        // TODO: Validate that ProviderConfigId.Value is not null/empty for issueProviderId,
        // repoProviderId, and agentProviderId. The previous string parameters had
        // ArgumentNullException.ThrowIfNull guards that are now lost because structs can't be null,
        // but default(ProviderConfigId) or implicit conversion from null still produces Value = null.

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping dispatch", issueIdentifier);
            return null;
        }

        var run = await ResolveAndCreateRunAsync(repoProviderId, agentProviderId, issueIdentifier,
            issueProviderId, agentId, brainProviderId, pipelineProviderId, initiatedBy, runType, ct);

        if (!_lifecycle.RegisterDispatchedRun(run))
            return null;

        _logger.Information(
            "Dispatched run {RunId} created for issue {IssueIdentifier} → agent {AgentId}",
            run.RunId, issueIdentifier, agentId);

        return run;
    }

    /// <inheritdoc />
    public async Task<RunReservation?> ReserveRunIdAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, string issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch")
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping reservation", issueIdentifier);
            return null;
        }

        // TODO: startedAt is captured before ResolveAndCreateRunAsync (provider resolution).
        // Original code captured it after provider resolution. If excluding provider resolution
        // latency from start time matters, move this assignment after the helper call.
        var startedAt = DateTimeOffset.UtcNow;

        var sentinel = await ResolveAndCreateRunAsync(repoProviderId, agentProviderId, issueIdentifier,
            issueProviderId, agentId, brainProviderId, pipelineProviderId, initiatedBy,
            PipelineRunType.Implementation, ct);

        if (!_lifecycle.RegisterDispatchedRun(sentinel))
            return null;

        _logger.Information(
            "Reserved run {RunId} for issue {IssueIdentifier}",
            sentinel.RunId, issueIdentifier);

        return new RunReservation(sentinel.RunId, sentinel.RepositoryName!, sentinel.ModelName!, startedAt);
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
        ProviderConfigId repoProviderId,
        ProviderConfigId agentProviderId,
        string issueIdentifier,
        ProviderConfigId issueProviderId,
        string? agentId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        PipelineRunType runType,
        CancellationToken ct)
    {
        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId.Value, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

        var run = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: issueIdentifier,
            issueTitle: string.Empty,
            issueProviderConfigId: issueProviderId.Value,
            repoProviderConfigId: repoProviderId.Value,
            runType: runType,
            initiatedBy: initiatedBy,
            agentId: agentId,
            agentProviderConfigId: agentProviderId.Value,
            brainProviderConfigId: brainProviderId);
        run.RepositoryName = tempRepoProvider.RepositoryFullName;
        run.ModelName = configuredModel;
        run.PipelineProviderConfigId = pipelineProviderId;

        return run;
    }
}
