using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Logging;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Concrete implementation of <see cref="IAgentHubFacade"/> that delegates to the
/// underlying orchestration services. Registered as a singleton in DI.
/// </summary>
public sealed class AgentHubFacade : IAgentHubFacade
{
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IWorkItemTransitionStore? _transitionStore;
    private readonly IWorkItemFallbackTransitionService? _workItemFallbackTransition;
    private readonly IProjectStore? _projectStore;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly ILogger<AgentHubFacadeDependencies> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentHubFacade(AgentHubFacadeDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.HistoryService);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProviderFactory);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _registry = deps.Registry;
        _runService = deps.RunService;
        _historyService = deps.HistoryService;
        _configStore = deps.ConfigStore;
        _providerFactory = deps.ProviderFactory;
        _logger = deps.Logger;
        _transitionStore = deps.TransitionStore;
        _workItemFallbackTransition = deps.WorkItemFallbackTransition;
        _projectStore = deps.ProjectStore;
        _pipelineConfigStore = deps.PipelineConfigStore;
        _timeProvider = deps.TimeProvider ?? TimeProvider.System;
    }

    // ── Registry operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId, bool preserveExistingConnectionId = false)
        => _registry.Register(message, connectionId, preserveExistingConnectionId);

    /// <inheritdoc />
    public bool Deregister(AgentId agentId)
        => _registry.Deregister(agentId);

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(AgentId agentId)
        => _registry.GetByAgentId(agentId);

    /// <inheritdoc />
    public Task<AgentEntry?> GetByAgentIdAsync(AgentId agentId, CancellationToken ct = default)
        => _registry.GetByAgentIdAsync(agentId, ct);

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
        => _registry.GetByConnectionId(connectionId);

    /// <inheritdoc />
    public void TransitionStatus(AgentId agentId, AgentStatus newStatus)
        => _registry.TransitionStatus(agentId, newStatus);

    /// <inheritdoc />
    public void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp)
        => _registry.UpdateHeartbeat(agentId, timestamp);

    // ── Run state operations ────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? GetRun(JobId jobId)
        => _runService.GetRun(jobId.Value);

    public void ReplaceRun(PipelineRun run)
        => _runService.ReplaceRun(run);

    /// <inheritdoc />
    public async Task<bool> TransitionWorkItemAsync(JobId jobId, WorkItemStatus status, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        if (_workItemFallbackTransition is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return true; // No DB configured — treat as success (no-op)

        // Single retry with longer backoff — acts as a safety net above the Polly pipeline
        // in WorkItemTransitionService (which handles transient DB errors with 5 retries).
        // This outer retry only fires if the entire Polly pipeline fails or the circuit breaks.
        // If all retries fail, ReconciliationService will eventually mark it Failed
        // (which may be incorrect if the agent actually succeeded).
        const int maxAttempts = 2;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (await _workItemFallbackTransition.TryFallbackChainAsync(workItemId, status, errorMessage, failureReason, ct))
                    return true;

                _logger.LogWarning(
                    "WorkItem {WorkItemId} transition to {Status} rejected (may already be terminal)",
                    workItemId, status);
                return false;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1
                && ex is not Polly.CircuitBreaker.BrokenCircuitException)
            {
                _logger.LogWarning(ex,
                    "WorkItem {WorkItemId} transition to {Status} failed on attempt {Attempt}, retrying",
                    workItemId, status, attempt + 1);
                // Wait 2s before final retry — gives brief recovery window after Polly exhaustion
                await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, ct);
            }
        }

        _logger.LogError(
            "WorkItem {WorkItemId} transition to {Status} failed after all retry attempts",
            workItemId, status);
        return false;
    }

    /// <inheritdoc />
    public void AddRun(PipelineRun run)
        => _runService.AddRun(run);

    /// <inheritdoc />
    public void AppendOutputLines(JobId jobId, IReadOnlyList<string> lines)
        => _runService.AppendOutputLines(jobId.Value, lines);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetOutputBacklogAsync(JobId jobId)
        => _runService.GetOutputBacklogAsync(jobId.Value);

    /// <inheritdoc />
    public void AppendChatEntry(JobId jobId, ChatEntry entry)
        => _runService.AppendChatEntry(jobId.Value, entry);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatEntry>> GetChatHistoryAsync(JobId jobId)
        => _runService.GetChatHistoryAsync(jobId.Value);

    /// <inheritdoc />
    public void RemoveRun(JobId jobId)
        => _runService.RemoveRun(jobId.Value);

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetActiveRunsByAgent(AgentId agentId)
        => _runService.GetActiveRuns().Where(r => r.AgentId == agentId.Value).ToList();

    // ── Dispatch operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<int> GetWorkItemRetryCountAsync(JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return 0;

        try
        {
            return await _transitionStore.GetRetryCountAsync(workItemId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get RetryCount for WorkItem {WorkItemId}", workItemId);
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task RequeueWorkItemAsync(JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        await _transitionStore.RequeueAsync(workItemId, ct);
        _logger.LogInformation("WorkItem {WorkItemId} re-queued as Pending (retry after rejection)", workItemId);
    }

    /// <inheritdoc />
    public async Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            return await _transitionStore.GetWorkItemProviderConfigIdsAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve provider config IDs from WorkItem {WorkItemId}", jobId.Value);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ResolveBrainReadOnlyAsync(JobId jobId, CancellationToken ct)
    {
        // Fail closed: any unresolvable condition means read-only.
        if (_pipelineConfigStore is null)
            return true;

        try
        {
            // Step 1: obtain repoProviderId and projectId.
            // Fast path — in-memory run (SignalR / same process).
            string? repoProviderId = null;
            string? projectId = null;

            var run = _runService.GetRun(jobId.Value);
            if (run is not null)
            {
                repoProviderId = run.RepoProviderConfigId;
                projectId = run.ProjectId;
            }
            else if (_transitionStore is not null && Guid.TryParse(jobId.Value, out var workItemId))
            {
                // Fallback — K8s mode where the run may not be in this process's memory.
                // NOTE (issue #3573): Guid.TryParse guard means a non-GUID job ID (e.g. a SignalR string
                // ID that is not GUID-formatted) with no in-memory run will always fail closed
                // (repoProviderId stays null → returns true) regardless of the actual BrainReadOnly
                // setting. Legitimate jobs with non-GUID string IDs in K8s mode would be denied
                // write access even for writable brains (Correctness finding).
                var configIds = await _transitionStore.GetWorkItemProviderConfigIdsAsync(workItemId, ct);
                repoProviderId = configIds?.RepoProviderConfigId;
                // NOTE (issue #3573): ProjectId is not stored in the WorkItem payload, so project-level
                // BrainReadOnly overrides are silently ignored in K8s mode during token refreshes.
                // If a project sets BrainReadOnly = true (overriding global false), the token refresh
                // will incorrectly issue a write token because PipelineConfigurationResolver is called
                // with project = null (skipping ApplyProjectOverrides). This is a security gap for
                // K8s-mode agents. Fix: store ProjectId in the WorkItem payload at dispatch time so
                // it can be retrieved here (Correctness & DotNetSpecialist findings).
                // ProjectId is not stored in the WorkItem payload, so we cannot resolve it here.
                // Proceed with project = null; ApplyProjectOverrides is a no-op for null projects.
            }

            if (string.IsNullOrEmpty(repoProviderId))
                return true; // fail closed — no repo, can't resolve template

            // Step 2: resolve the owning project (if available).
            PipelineProject? project = null;
            if (!string.IsNullOrEmpty(projectId) && _projectStore is not null)
                project = await _projectStore.GetProjectByIdAsync(projectId, ct);

            // Step 3: resolve BrainReadOnly via the full config chain.
            // LoadAllTemplatesAsync comes from IProjectStore (which _projectStore implements)
            // or from the IPipelineConfigStore (which has no template access). We need the
            // project store's LoadAllTemplatesAsync for template-level overrides.
            Func<CancellationToken, Task<IReadOnlyList<PipelineJobTemplate>>> loadTemplates =
                _projectStore is not null
                    ? _projectStore.LoadAllTemplatesAsync
                    : _ => Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(Array.Empty<PipelineJobTemplate>());

            // Raw provider configs are not needed for BrainReadOnly resolution (it is not
            // derived from config settings); pass an empty list — ApplyBlacklistOverride is a
            // no-op for configs with no BlacklistedPaths, and BrainReadOnly comes from the
            // template, not from provider config settings.
            // NOTE (issue #3573): this assumption — that BrainReadOnly is not influenced by provider
            // config settings — is not enforced by the interface or an assertion. If
            // PipelineConfigurationResolver.ResolveAsync ever adds a code path that reads provider
            // config metadata to influence BrainReadOnly, passing an empty list here would silently
            // produce a different resolved value than the dispatch path (which passes real raw configs).
            // Add an interface guarantee or an assertion to make this contract explicit
            // (Correctness & DotNetSpecialist findings).
            var resolvedConfig = await PipelineConfigurationResolver.ResolveAsync(
                _pipelineConfigStore.LoadPipelineConfigAsync,
                loadTemplates,
                project,
                (ProviderConfigId)repoProviderId,
                Array.Empty<ProviderConfig>(),
                ct);

            return resolvedConfig.BrainReadOnly;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "ResolveBrainReadOnlyAsync: failed to resolve BrainReadOnly for job {JobId}; failing closed (read-only)",
                jobId.Value);
            return true; // fail closed
        }
    }

    // ── History ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
        => _historyService.AddRunToHistoryAsync(run, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => _historyService.GetRunHistoryAsync(ct);

    // ── Issue provider operations ───────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
    {
        if (_projectStore is null)
            return Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(Array.Empty<PipelineJobTemplate>());
        return _projectStore.LoadTemplatesForProjectAsync(projectId, ct);
    }

    /// <inheritdoc />
    public Task<PipelineProject?> GetProjectByIdAsync(string projectId, CancellationToken ct)
    {
        if (_projectStore is null)
            return Task.FromResult<PipelineProject?>(null);
        return _projectStore.GetProjectByIdAsync(projectId, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
        => _configStore.LoadProviderConfigsAsync(kind, ct);

    /// <inheritdoc />
    public Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
        => _configStore.GetProviderConfigByIdAsync(id, kind, ct);

    /// <inheritdoc />
    public IIssueProvider CreateIssueProvider(ProviderConfig config)
        => _providerFactory.CreateIssueProvider(config);

    /// <inheritdoc />
    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config)
        => _providerFactory.CreateRepositoryProvider(config);

    // ── Progress tracking ───────────────────────────────────────────────

    /// <inheritdoc />
    public async Task TouchLastProgressAsync(JobId jobId, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        // The throttle (skip when the DB value is recent enough) lives in the store's
        // TouchLastProgressAsync — the facade only translates failures into telemetry.
        try
        {
            await _transitionStore.TouchLastProgressAsync(workItemId, timestamp, ct);
        }
        catch (Exception ex)
        {
            WorkDistributionTelemetry.ProgressWriteFailures.Add(1);
            _logger.LogWarning(ex, "Failed to update LastProgressAt for WorkItem {WorkItemId}", workItemId);
        }
    }

    /// <inheritdoc />
    public async Task RecordBranchNameAsync(JobId jobId, string branchName, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        try
        {
            await _transitionStore.RecordBranchNameAsync(workItemId, branchName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record branch {BranchName} for WorkItem {WorkItemId}", LogSanitizer.SanitizeForLog(branchName), workItemId);
        }
    }

    /// <inheritdoc />
    public async Task<(string IssueIdentifier, string IssueProviderConfigId)?> GetWorkItemIssueMetadataAsync(
        JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            return await _transitionStore.GetWorkItemIssueMetadataAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read issue metadata from WorkItem {WorkItemId}", jobId.Value);
            return null;
        }
    }

    /// <inheritdoc />
    public bool CanVerifyWorkItems => _transitionStore is not null;

    /// <inheritdoc />
    public Task<WorkItemRunRecord?> GetWorkItemRunRecordAsync(JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var id))
            return Task.FromResult<WorkItemRunRecord?>(null);

        // No catch: a failed read must not look like "no such work item" to the ownership check.
        return _transitionStore.GetWorkItemRunRecordAsync(id, ct);
    }

    /// <inheritdoc />
    public Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
        => _registry.UpdateAgentFieldAsync(agentId, field, value);

    /// <inheritdoc />
    public void SetLocalAgentSnapshotField(AgentId agentId, string field, string? value)
        => _registry.SetLocalSnapshotField(agentId, field, value);
}
