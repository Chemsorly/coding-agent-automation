using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs and managing harness suggestions.
/// </summary>
public sealed class ConsolidationService : IConsolidationService
{
    private readonly ILogger _logger;
    private readonly PipelineConfiguration _config;
    private readonly IHarnessSuggestionStore _harnessSuggestionStore;
    private readonly ConsolidationTemplateResolver _templateResolver;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IConsolidationSelectorResolver? _selectorResolver;
    private readonly IPipelineConfigStore? _pipelineConfigStore;

    /// <inheritdoc />
    public event Action? OnChange;

    public ConsolidationService(
        ConsolidationServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.Config);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.HarnessSuggestionStore);

        _logger = deps.Logger;
        _config = deps.Config;
        _harnessSuggestionStore = deps.HarnessSuggestionStore;
        _templateResolver = new ConsolidationTemplateResolver(deps.ProjectStore);
        _providerConfigStore = deps.ProviderConfigStore;
        _workDistributor = deps.WorkDistributor;
        _selectorResolver = deps.SelectorResolver;
        _pipelineConfigStore = deps.PipelineConfigStore;
    }

    /// <inheritdoc />
    public async Task<ConsolidationTriggerResult?> TriggerAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        CancellationToken ct,
        bool autoDispatch = false)
    {
        var templateIdValue = templateId?.Value;

        // ── 1. Resolve template + project ────────────────────────────────────
        PipelineJobTemplate? template = null;
        PipelineProject? project = null;
        ProviderConfig? repoConfig = null;

        if (templateId is not null)
        {
            (template, project) = await _templateResolver.ResolveTemplateAndProjectAsync(templateIdValue!, ct);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateIdValue);
                return null;
            }

            // Resolve repo ProviderConfig (for selector resolution).
            repoConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                template.RepoProviderId, ProviderKind.Repository, ct);
        }

        var templateName = template?.Name ?? "Global";
        var projectName = project?.Name;
        var projectId = project?.Id;

        // Resolve repoProviderId and brainProviderId for the JobDistributionRequest payload.
        // AgentTokenRefreshService reads RepoProviderConfigId directly from WorkItems.Payload
        // (JSONB) and will fail with HubException if it is empty for template-scoped runs.
        var repoProviderId = template?.RepoProviderId ?? "";
        var brainProviderId = template?.BrainProviderId;

        // The configuration the job runs with: the live global settings with the project's overrides,
        // as the agent resolves them at claim time. The bootstrap _config is only a fallback for hosts
        // without a configuration store (tests); it never reflects saved settings.
        var globalConfig = _pipelineConfigStore is not null
            ? await _pipelineConfigStore.LoadPipelineConfigAsync(ct)
            : _config;
        var config = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);

        // Brain consolidation writes to the brain, so it does not run from a template whose brain is read-only.
        if (type == ConsolidationRunType.BrainConsolidation && template is not null
            && ConsolidationTemplateFilter.IsBrainReadOnly(template, project, globalConfig))
        {
            _logger.Warning(
                "Consolidation run rejected: the brain of template {TemplateName} is read-only, so brain consolidation does not run from it",
                templateName);
            return null;
        }

        // ── 2. Guard: WorkDistributor required ───────────────────────────────
        if (_workDistributor is null)
        {
            // Should never happen in production (AddConsolidationServices always injects it).
            // In tests that don't provide a distributor, this surfaces a clear diagnostic.
            _logger.Error(
                "ConsolidationService: IWorkDistributor is not configured — cannot dispatch run for {Type}/{TemplateId}",
                type, templateIdValue ?? "Global");
            throw new InvalidOperationException(
                "ConsolidationService requires IWorkDistributor to be injected via ConsolidationServiceDependencies. " +
                "Ensure AddConsolidationServices passes WorkDistributor.");
        }

        // ── 3. Resolve agent selector labels ─────────────────────────────────
        var selectorLabels = await ResolveSelectorLabelsAsync(repoConfig, config, type, templateIdValue, ct);
        if (selectorLabels is null)
            return null;

        // ── 4. Build a unique RunId for this trigger ──────────────────────────
        var runId = Guid.NewGuid().ToString();
        var traceContext = PipelineTelemetry.CaptureTraceContext("TriggerConsolidation");

        // ── 5. Build and submit the JobDistributionRequest ───────────────────
        // IssueIdentifier format: "{type}:{scope}" (issue #3027). The scope is what the run works on:
        // the brain for brain consolidation, the template (and so its repository) for a refactoring scan,
        // "global" for harness suggestions. Many templates can share one brain, and two consolidations of
        // one brain would race to push to it, so templates that share a brain share the key.
        // This deterministic format feeds the partial unique index on
        // (IssueIdentifier, IssueProviderConfigId) for non-terminal statuses in
        // PipelineDbContext.OnModelCreating, providing cross-replica dedup.
        var scope = ResolveRunScope(type, brainProviderId, templateIdValue);
        var issueIdentifier = $"{type}:{scope}";

        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = repoProviderId,
            BrainProviderConfigId = brainProviderId,
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = AgentSelectorKey.From(selectorLabels),
            TimeoutSeconds = (int)config.AgentTimeout.TotalSeconds,
            ConsolidationRunType = type,
            ConsolidationTemplateId = templateIdValue,
            AutoDispatch = autoDispatch,
            ProjectId = !string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var pid)
                ? pid
                : (Guid?)null,
            ProjectName = projectName,
            // TraceContext captured before dispatch so the resulting WorkItem inherits
            // the originating trace even when dispatched through the API asynchronously.
            TraceContext = traceContext
        };

        var result = await TryDistributeAsync(_workDistributor, request, type, templateIdValue, ct);
        if (result is null)
            return null;

        // ── 6. Detect duplicate rejection from the API layer ─────────────────
        // KubernetesWorkDistributor maps a 409 Conflict from POST /api/work-items to
        // DistributionResult(Success: true, WorkItemId: null, Queued: true, AlreadyExists: true).
        // This happens when the partial unique index on (IssueIdentifier, IssueProviderConfigId)
        // rejects a duplicate insert because a live WorkItem already exists for this consolidation type.
        if (result.AlreadyExists)
        {
            _logger.Warning(
                "ConsolidationService: duplicate rejected for {Type}/{TemplateId} — " +
                "a live WorkItem already exists (API returned 409).",
                type, templateIdValue ?? "Global");
            return null;
        }

        // ── 7. Build and return the result ────────────────────────────────────
        // TODO [WARNING]: StartedAtUtc is captured here, after DistributeAsync returns. Under load,
        // DistributeAsync (HTTP call to the Pipeline API) can take several seconds, so StartedAtUtc
        // on the returned record can be materially later than the actual start of the consolidation
        // operation. This affects the accuracy of the "Started" column in the run-history table.
        // Fix: capture DateTimeOffset.UtcNow before the DistributeAsync call (step 4) and pass it
        // through, or record the timestamp at WorkItem creation time in the API layer.
        var triggerResult = new ConsolidationTriggerResult(
            RunId: runId,
            Type: type,
            TemplateId: templateIdValue,
            TemplateName: templateName,
            ProjectId: projectId,
            ProjectName: projectName,
            StartedAtUtc: DateTimeOffset.UtcNow,
            WorkItemId: result.WorkItemId);

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName} (WorkItem {WorkItemId} created as Pending)",
            runId, type, templateName, result.WorkItemId);
        OnChange?.Invoke();
        return triggerResult;
    }

    /// <summary>
    /// Resolves the agent selector labels for a run. Returns null when the selector resolver
    /// has no agent profiles available yet (startup race), which the caller treats as a transient failure.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveSelectorLabelsAsync(
        ProviderConfig? repoConfig,
        PipelineConfiguration config,
        ConsolidationRunType type,
        string? templateIdValue,
        CancellationToken ct)
    {
        if (_selectorResolver is null)
        {
            // Fallback when no resolver is injected (tests, or legacy call sites).
            // Use LabelResolver which reads from repoConfig + DefaultRequiredAgentLabels.
            return LabelResolver.ResolveRequiredLabels(repoConfig, config);
        }

        var selectorLabels = await _selectorResolver.ResolveAsync(repoConfig, config, ct);
        if (selectorLabels is null)
        {
            // Null = startup race (no profiles available yet). Treat as transient failure.
            _logger.Warning(
                "ConsolidationService: no agent profiles available for {Type}/{TemplateId} — " +
                "re-trigger after profiles are loaded",
                type, templateIdValue ?? "Global");
        }
        return selectorLabels;
    }

    /// <summary>
    /// Resolves the scope part of the run's IssueIdentifier: the brain for brain consolidation,
    /// otherwise the template, or "global" when the run is not template-scoped.
    /// </summary>
    private static string ResolveRunScope(ConsolidationRunType type, string? brainProviderId, string? templateIdValue)
    {
        return type == ConsolidationRunType.BrainConsolidation && !string.IsNullOrEmpty(brainProviderId)
            ? brainProviderId
            : templateIdValue ?? "global";
    }

    /// <summary>
    /// Submits <paramref name="request"/> to the work distributor. Returns null (after logging) when
    /// the call throws or the distributor reports a permanent or transient failure.
    /// </summary>
    private async Task<DistributionResult?> TryDistributeAsync(
        IWorkDistributor workDistributor,
        JobDistributionRequest request,
        ConsolidationRunType type,
        string? templateIdValue,
        CancellationToken ct)
    {
        DistributionResult result;
        try
        {
            result = await workDistributor.DistributeAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "ConsolidationService: unexpected error calling DistributeAsync for {Type}/{TemplateId}",
                type, templateIdValue ?? "Global");
            return null;
        }

        if (result.Success)
            return result;

        if (result.IsPermanentFailure)
        {
            // Permanent failure (e.g. no job template for the resolved selector).
            PipelineTelemetry.ConsolidationDispatchPermanentFailures.Add(1,
                new KeyValuePair<string, object?>("run.type", type.ToString()));
            _logger.Error(
                "ConsolidationService: permanent dispatch failure for {Type}/{TemplateId}: {Error}. " +
                "No WorkItem created. Re-trigger after fixing the agent configuration.",
                type, templateIdValue ?? "Global", result.ErrorMessage);
        }
        else
        {
            // Transient failure (capacity limit, PVC unavailable, etc.).
            _logger.Warning(
                "ConsolidationService: transient dispatch failure for {Type}/{TemplateId}: {Error}. " +
                "Re-trigger to retry.",
                type, templateIdValue ?? "Global", result.ErrorMessage);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<HarnessSuggestions?> GetHarnessSuggestionsAsync(CancellationToken ct)
    {
        try { return await _harnessSuggestionStore.LoadAsync(ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to read harness suggestions"); return null; }
    }

    /// <inheritdoc />
    public async Task SaveHarnessSuggestionsAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        try
        {
            await _harnessSuggestionStore.SaveAsync(suggestions, ct);
            _logger.Information("Harness suggestions saved");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save harness suggestions");
        }
    }
}
