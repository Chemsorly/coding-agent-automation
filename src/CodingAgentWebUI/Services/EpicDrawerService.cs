using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Manages the epic dispatch drawer lifecycle: loading epics with deduplication,
/// label filtering, pagination, and decomposition dispatch.
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt; for epic items.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class EpicDrawerService : IEpicDrawerService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<EpicDrawerService>();

    private readonly IProviderFactory _providerFactory;
    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentRegistryService _agentRegistry;
    private readonly IDispatchOrchestrationService _dispatchOrchestration;

    private readonly DrawerStateService<IssueSummary> _epicDrawer;

    public EpicDrawerService(
        IProviderFactory providerFactory,
        IWorkDistributor workDistributor,
        IAgentRegistryService agentRegistry,
        IDispatchOrchestrationService dispatchOrchestration)
    {
        _providerFactory = providerFactory;
        _workDistributor = workDistributor;
        _agentRegistry = agentRegistry;
        _dispatchOrchestration = dispatchOrchestration;

        _epicDrawer = new DrawerStateService<IssueSummary>(
            t => LoadEpicDrawerIssuesAsync(t, 1),
            LoadEpicDrawerLabelsAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception. Consider returning (false, "Use DispatchFromEpicDrawerAsync", null)
            // instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromEpicDrawerAsync on the coordinator"),
            closeOnDispatch: true);
    }

    // ── IEpicDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _epicDrawer;

    // ── Cached context (set at open/switch time) ──

    private IReadOnlyList<ProviderConfig>? _cachedIssueProviders;
    private IReadOnlyList<PipelineProject>? _cachedProjects;

    // ── Data loading ──

    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
    {
        _epicDrawer.Loading = true;
        _epicDrawer.Page = page;
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId)
                ? parentProject.EpicIssueProviderId
                : template.IssueProviderId;
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) { _epicDrawer.Loading = false; return "Epic issue provider not found."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);

            var epicLabels = new List<string> { AgentLabels.Epic };
            if (_epicDrawer.SelectedLabels.Count > 0)
                epicLabels.AddRange(_epicDrawer.SelectedLabels);

            var approvedLabels = new List<string> { AgentLabels.EpicApproved };
            if (_epicDrawer.SelectedLabels.Count > 0)
                approvedLabels.AddRange(_epicDrawer.SelectedLabels);

            var epicResult = await provider.ListOpenIssuesAsync(page, 8, epicLabels, CancellationToken.None);
            var approvedResult = await provider.ListOpenIssuesAsync(page, 8, approvedLabels, CancellationToken.None);

            _epicDrawer.Items = epicResult.Items.Concat(approvedResult.Items)
                .GroupBy(i => i.Identifier)
                .Select(g => g.First())
                .ToList();
            _epicDrawer.HasMore = epicResult.HasMore || approvedResult.HasMore;
            return null;
        }
        catch (Exception ex) { _epicDrawer.Items.Clear(); return $"Failed to load epics: {ex.Message}"; }
        finally { _epicDrawer.Loading = false; }
    }

    private async Task<string?> LoadEpicDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId)
                ? parentProject.EpicIssueProviderId
                : template.IssueProviderId;
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            _epicDrawer.Labels = labels.Where(l => !l.StartsWith(AgentLabels.Epic, StringComparison.OrdinalIgnoreCase)).ToList();
            return null;
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to load epic drawer labels"); _epicDrawer.Labels.Clear(); return null; }
    }

    public void ClearEpicDrawerIssues()
    {
        _epicDrawer.Items.Clear();
        _epicDrawer.Page = 1;
        _epicDrawer.HasMore = false;
        _epicDrawer.SelectedLabels.Clear();
    }

    private PipelineProject? GetParentProject(string templateId)
        => _cachedProjects?.FirstOrDefault(p => p.TemplateIds.Contains(templateId));

    // ── Dispatch ──

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        if (!issueProviders.Any(p => p.Id == template.IssueProviderId) || !repoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (_workDistributor.RequiresConnectedAgents && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var phaseType = issue.Labels.Contains(AgentLabels.EpicApproved, StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
        var phaseLabel = phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

        return await DrawerDispatchHelper.DispatchWithOrchestrationAsync(
            _dispatchOrchestration,
            project => _dispatchOrchestration.PrepareDecompositionDistributionRequestAsync(
                new DecompositionDispatchOrchestrationRequest
                {
                    EpicIdentifier = issue.Identifier,
                    EpicTitle = issue.Title ?? "",
                    PhaseType = phaseType,
                    IssueProviderId = template.IssueProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    InitiatedBy = DrawerDispatchHelper.ManualInitiator,
                    Project = project
                }, CancellationToken.None),
            parentProject ?? new PipelineProject { Id = "", Name = "Unknown" },
            "Could not dispatch — epic is already being processed or queued, or no agents are available.",
            $"⏳ Queued epic #{issue.Identifier} for {phaseLabel} — the job controller will start an agent pod for it",
            $"✅ Dispatched epic #{issue.Identifier} for {phaseLabel}");
    }

    // ── Drawer orchestration ──

    public async Task<string?> OpenEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        IReadOnlyList<PipelineProject> projects,
        Func<Task>? notifyStateChanged = null)
    {
        _cachedProjects = projects;
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        return await _epicDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawer.Close();

    public Task<string?> SwitchToEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        IReadOnlyList<PipelineProject> projects,
        Func<Task>? notifyStateChanged = null)
    {
        _cachedProjects = projects;
        return _epicDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _epicDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        _epicDrawer.IsDispatching = true;
        try
        {
            if (_epicDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchDecompositionAsync(issue, _epicDrawer.Template, issueProviders, repoProviders, parentProject);
            if (success)
                _epicDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _epicDrawer.IsDispatching = false; }
    }

    // ── Cross-drawer coordination ──

    public void Hide() => _epicDrawer.IsOpen = false;

    // ── Provider context injection ──

    internal void SetProviderContext(IReadOnlyList<ProviderConfig> issueProviders)
    {
        _cachedIssueProviders = issueProviders;
    }

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _epicDrawer.Dispose();
        _disposed = true;
    }
}
