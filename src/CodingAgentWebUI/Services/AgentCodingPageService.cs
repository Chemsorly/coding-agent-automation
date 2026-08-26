using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Coordinator for the AgentCoding page — owns template CRUD, loop controls, provider lists, and
/// forwards drawer operations to the focused drawer services.
/// The Blazor component delegates to this service and retains only UI state.
/// Registered as Scoped because it holds per-page mutable state.
/// Config and project data are loaded via <see cref="IPipelineApiConfigClient"/>.
/// </summary>
public class AgentCodingPageService
{
    private readonly ISchedulerApiClient _schedulerClient;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IIssueDrawerService _issueDrawerService;
    private readonly IPrReviewDrawerService _prReviewDrawerService;
    private readonly IEpicDrawerService _epicDrawerService;

    public AgentCodingPageService(
        ISchedulerApiClient schedulerClient,
        IPipelineApiConfigClient configClient,
        IIssueDrawerService issueDrawerService,
        IPrReviewDrawerService prReviewDrawerService,
        IEpicDrawerService epicDrawerService)
    {
        _schedulerClient = schedulerClient;
        _configClient = configClient;
        _issueDrawerService = issueDrawerService;
        _prReviewDrawerService = prReviewDrawerService;
        _epicDrawerService = epicDrawerService;
    }

    // ── Configuration state ──

    public List<PipelineJobTemplate> Templates { get; private set; } = [];
    public IReadOnlyList<PipelineProject> Projects { get; private set; } = [];
    public List<ProviderConfig> IssueProviders { get; private set; } = [];
    public List<ProviderConfig> RepoProviders { get; private set; } = [];
    public List<ProviderConfig> PipelineProviders { get; private set; } = [];
    public List<ProviderConfig> BrainProviders { get; private set; } = [];
    public IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; private set; } = [];
    public IReadOnlyList<ReviewerConfiguration> ReviewerConfigs { get; private set; } = [];
    public IReadOnlyList<AgentProfile> AgentProfiles { get; private set; } = [];
    public PipelineConfiguration PipelineConfig { get; private set; } = new();
    public int MaxRetries { get; private set; } = 3;

    // ── Drawer state accessors (forwarded to drawer services) ──

    public DrawerStateService<IssueSummary> IssueDrawer => _issueDrawerService.DrawerState;
    public DrawerStateService<PullRequestSummary> PrDrawer => _prReviewDrawerService.DrawerState;
    public DrawerStateService<IssueSummary> EpicDrawer => _epicDrawerService.DrawerState;

    public bool IsIssueDrawerOpen => _issueDrawerService.DrawerState.IsOpen;
    public bool IsPrDrawerOpen => _prReviewDrawerService.DrawerState.IsOpen;
    public bool IsEpicDrawerOpen => _epicDrawerService.DrawerState.IsOpen;
    public PipelineJobTemplate? IssueDrawerTemplate => _issueDrawerService.DrawerState.Template;
    public PipelineJobTemplate? PrDrawerTemplate => _prReviewDrawerService.DrawerState.Template;
    public PipelineJobTemplate? EpicDrawerTemplate => _epicDrawerService.DrawerState.Template;
    public bool IssueDrawerDispatching { get => _issueDrawerService.DrawerState.IsDispatching; set => _issueDrawerService.DrawerState.IsDispatching = value; }
    public bool PrDrawerDispatching { get => _prReviewDrawerService.DrawerState.IsDispatching; set => _prReviewDrawerService.DrawerState.IsDispatching = value; }
    public bool EpicDrawerDispatching { get => _epicDrawerService.DrawerState.IsDispatching; set => _epicDrawerService.DrawerState.IsDispatching = value; }

    public List<IssueSummary> DrawerIssues => _issueDrawerService.DrawerState.Items;
    public int DrawerPage => _issueDrawerService.DrawerState.Page;
    public bool DrawerHasMore => _issueDrawerService.DrawerState.HasMore;
    public bool DrawerLoading => _issueDrawerService.DrawerState.Loading;
    public List<string> DrawerLabels => _issueDrawerService.DrawerState.Labels;
    public List<string> DrawerSelectedLabels => _issueDrawerService.DrawerState.SelectedLabels;

    public List<PullRequestSummary> PrDrawerPrs => _prReviewDrawerService.DrawerState.Items;
    public int PrDrawerPage => _prReviewDrawerService.DrawerState.Page;
    public bool PrDrawerHasMore => _prReviewDrawerService.DrawerState.HasMore;
    public bool PrDrawerLoading => _prReviewDrawerService.DrawerState.Loading;
    public List<string> PrDrawerLabels => _prReviewDrawerService.DrawerState.Labels;
    public List<string> PrDrawerSelectedLabels => _prReviewDrawerService.DrawerState.SelectedLabels;

    public List<IssueSummary> EpicDrawerIssues => _epicDrawerService.DrawerState.Items;
    public int EpicDrawerPage => _epicDrawerService.DrawerState.Page;
    public bool EpicDrawerHasMore => _epicDrawerService.DrawerState.HasMore;
    public bool EpicDrawerLoading => _epicDrawerService.DrawerState.Loading;
    public List<string> EpicDrawerLabels => _epicDrawerService.DrawerState.Labels;
    public List<string> EpicDrawerSelectedLabels => _epicDrawerService.DrawerState.SelectedLabels;

    /// <summary>Forwarded to IssueDrawerService — used by the coordinator and all three drawer components.</summary>
    public Dictionary<string, DependencyCheckResult> DrawerReadiness => _issueDrawerService.DrawerReadiness;

    private const string DrawerTabIssue = "issue";
    private const string DrawerTabPr = "pr";
    private const string DrawerTabEpic = "epic";

    public string ActiveDrawerTab
    {
        get
        {
            if (IsIssueDrawerOpen) return DrawerTabIssue;
            if (IsPrDrawerOpen) return DrawerTabPr;
            if (IsEpicDrawerOpen) return DrawerTabEpic;
            return "";
        }
    }

    public PipelineJobTemplate? ActiveDrawerTemplate => ActiveDrawerTab switch
    {
        DrawerTabIssue => IssueDrawerTemplate,
        DrawerTabPr => PrDrawerTemplate,
        DrawerTabEpic => EpicDrawerTemplate,
        _ => null
    };

    // ── Initialization ──

    public async Task<string?> InitializeAsync()
    {
        try
        {
            IssueProviders = (await _configClient.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, CancellationToken.None)).ToList();
            var allRepoProviders = (await _configClient.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository, CancellationToken.None)).ToList();
            PipelineProviders = (await _configClient.GetProviderConfigsWithSecretsAsync(ProviderKind.Pipeline, CancellationToken.None)).ToList();
            BrainProviders = allRepoProviders.Where(p => p.RepositoryRole == RepositoryRole.Brain).ToList();
            RepoProviders = allRepoProviders.Where(p => p.RepositoryRole != RepositoryRole.Brain).ToList();

            var config = await _configClient.GetPipelineConfigAsync(CancellationToken.None);
            MaxRetries = config.MaxRetries;
            Templates = (await _configClient.GetAllTemplatesAsync(CancellationToken.None)).ToList();
            PipelineConfig = config;
            Projects = await _configClient.GetProjectsAsync(CancellationToken.None);
            QualityGateConfigs = await _configClient.GetQualityGateConfigsAsync(CancellationToken.None);
            ReviewerConfigs = await _configClient.GetReviewerConfigsAsync(CancellationToken.None);
            AgentProfiles = await _configClient.GetAgentProfilesAsync(CancellationToken.None);

            // Push provider context into drawer services so load callbacks have access
            PropagateProviderContext();

            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to load configuration: {ex.Message}";
        }
    }

    /// <summary>
    /// Pushes the loaded provider lists into the drawer services.
    /// Must be called after InitializeAsync populates IssueProviders / RepoProviders.
    /// Also called by the drawer open methods to ensure context is fresh.
    /// </summary>
    private void PropagateProviderContext()
    {
        // TODO: [WARNING] These concrete type-casts silently no-op if the registered implementation is
        // replaced by a decorator, proxy, or test double. If the cast fails, _cachedIssueProviders /
        // _cachedRepoProviders remain null in the drawer service, causing all load calls to return
        // "provider not found" without surfacing a clear error. Fix: lift SetProviderContext onto
        // IIssueDrawerService, IPrReviewDrawerService, and IEpicDrawerService, or pass provider lists
        // as parameters to each load method (matching the pattern already used for DispatchIssueAsync).
        if (_issueDrawerService is IssueDrawerService issueImpl)
            issueImpl.SetProviderContext(IssueProviders, RepoProviders);
        if (_prReviewDrawerService is PrReviewDrawerService prImpl)
            prImpl.SetProviderContext(IssueProviders, RepoProviders);
        if (_epicDrawerService is EpicDrawerService epicImpl)
            epicImpl.SetProviderContext(IssueProviders);
    }

    // ── Template Operations ──

    public Task<(bool Success, string? Error)> ToggleTemplateEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { Enabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleImplementationEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { ImplementationEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleReviewEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { ReviewEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleDecompositionEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { DecompositionEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleHousekeepingEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { HousekeepingEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleBranchCleanupEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { HousekeepingBranchCleanupEnabled = e }, enabled);

    private async Task<(bool Success, string? Error)> TogglePropertyAsync(
        PipelineJobTemplate template,
        Func<PipelineJobTemplate, bool, PipelineJobTemplate> updater,
        bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = updater(template, enabled);
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _configClient.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}"); }
        Templates[idx] = updated;
        return (true, null);
    }

    public (bool Valid, string? FormError) ValidateAddTemplate(TemplateTableSection.TemplateFormModel form)
    {
        if (string.IsNullOrWhiteSpace(form.Name)) return (false, "Name is required.");
        if (string.IsNullOrEmpty(form.IssueProviderId)) return (false, "Issue Provider is required.");
        if (string.IsNullOrEmpty(form.RepoProviderId)) return (false, "Repo Provider is required.");
        if (Templates.Any(t => t.IssueProviderId == form.IssueProviderId && t.RepoProviderId == form.RepoProviderId))
            return (false, "A template with the same Issue Provider + Repo Provider combination already exists.");
        return (true, null);
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> AddTemplateAsync(TemplateTableSection.TemplateFormModel form)
    {
        var newTemplate = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(), Name = form.Name.Trim(),
            IssueProviderId = form.IssueProviderId, RepoProviderId = form.RepoProviderId,
            BrainProviderId = string.IsNullOrEmpty(form.BrainProviderId) ? null : form.BrainProviderId,
            PipelineProviderId = string.IsNullOrEmpty(form.PipelineProviderId) ? null : form.PipelineProviderId,
            BrainReadOnly = form.BrainReadOnly, ImplementationEnabled = form.ImplementationEnabled,
            ReviewEnabled = form.ReviewEnabled, DecompositionEnabled = form.DecompositionEnabled,
            HousekeepingEnabled = form.HousekeepingEnabled,
            HousekeepingConcurrencyLimit = form.HousekeepingConcurrencyLimit,
            HousekeepingBranchCleanupEnabled = form.HousekeepingBranchCleanupEnabled,
            Enabled = true
        };
        var targetProjectId = string.IsNullOrEmpty(form.ProjectId) ? WellKnownIds.DefaultProjectId : form.ProjectId;
        try { await _configClient.SaveTemplateAsync(targetProjectId, newTemplate, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Templates.Add(newTemplate);
        Projects = await _configClient.GetProjectsAsync(CancellationToken.None);
        return (true, null, $"Template \"{newTemplate.Name}\" added.");
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> RemoveTemplateAsync(PipelineJobTemplate template)
    {
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _configClient.DeleteTemplateAsync(projectId, template.Id, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to delete: {ex.Message}", null); }
        Templates.RemoveAll(t => t.Id == template.Id);
        Projects = await _configClient.GetProjectsAsync(CancellationToken.None);
        return (true, null, $"Template \"{template.Name}\" removed.");
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> MoveTemplateToProjectAsync(
        TemplateId templateId, string sourceProjectId, string targetProjectId)
    {
        try
        {
            var sourceProject = Projects.FirstOrDefault(p => p.Id == sourceProjectId);
            var targetProject = Projects.FirstOrDefault(p => p.Id == targetProjectId);
            if (sourceProject == null || targetProject == null) return (true, null, null);
            await _configClient.SaveProjectAsync(sourceProject with { TemplateIds = sourceProject.TemplateIds.Where(id => id != templateId.Value).ToList() }, CancellationToken.None);
            await _configClient.SaveProjectAsync(targetProject with { TemplateIds = targetProject.TemplateIds.Append(templateId.Value).ToList() }, CancellationToken.None);
            Projects = await _configClient.GetProjectsAsync(CancellationToken.None);
            return (true, null, $"Moved \"{Templates.FirstOrDefault(t => t.Id == templateId.Value)?.Name ?? templateId.Value}\" to {targetProject.Name}.");
        }
        catch (Exception ex) { return (false, $"Failed to move template: {ex.Message}", null); }
    }

    // ── Loop Controls ──

    public async Task<(bool Success, string? Error)> StartLoopAsync()
    {
        try
        {
            var result = await _schedulerClient.StartLoopAsync(CancellationToken.None);
            // Persistence (ClosedLoopAutoStart=true) is handled by the Scheduler endpoint.
            return (result.Started, result.Error);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start loop: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> StopLoopAsync()
    {
        try
        {
            await _schedulerClient.StopLoopAsync(CancellationToken.None);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop loop: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> ResumeLoopAsync()
    {
        try
        {
            await _schedulerClient.ResumeLoopAsync(CancellationToken.None);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to resume loop: {ex.Message}");
        }
    }

    // ── Issue drawer forwarding wrappers ──

    /// <summary>Public pagination-aware loader for issue drawer (called directly from AgentCoding.razor.cs).</summary>
    public Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
        => _issueDrawerService.LoadDrawerIssuesAsync(template, page);

    public Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page)
        => _issueDrawerService.LoadDrawerIssuesPageAsync(template, page);

    public Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
        => _issueDrawerService.LoadDrawerLabelsAsync(template);

    /// <summary>
    /// Checks dependency readiness for all current drawer issues (called directly from AgentCoding.razor.cs).
    /// </summary>
    public Task CheckDrawerDependenciesAsync(PipelineJobTemplate template, Action? onProgress, CancellationToken cancellationToken)
        => _issueDrawerService.CheckDrawerDependenciesAsync(template, onProgress, cancellationToken);

    public void ClearDrawerIssues() => _issueDrawerService.ClearDrawerIssues();

    // ── PR drawer forwarding wrappers ──

    /// <summary>Public pagination-aware loader for PR drawer (called directly from AgentCoding.razor.cs).</summary>
    public Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page)
        => _prReviewDrawerService.LoadPrDrawerPageAsync(template, page);

    public void ClearPrDrawerLabelFilter() => _prReviewDrawerService.ClearPrDrawerLabelFilter();

    // ── Epic drawer forwarding wrappers ──

    /// <summary>Public pagination-aware loader for epic drawer (called directly from AgentCoding.razor.cs).</summary>
    public Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
        => _epicDrawerService.LoadEpicDrawerIssuesAsync(template, page);

    public void ClearEpicDrawerIssues() => _epicDrawerService.ClearEpicDrawerIssues();

    // ── Active issues forwarding ──

    public Task RefreshActiveIssuesAsync() => _issueDrawerService.RefreshActiveIssuesAsync();

    public bool IsIssueActive(IssueIdentifier issueIdentifier, string issueProviderConfigId)
        => _issueDrawerService.IsIssueActive(issueIdentifier, issueProviderConfigId);

    /// <summary>
    /// Checks if an issue is currently distributed (Pending, Dispatched, or Running).
    /// Used by drawer components to show processing status.
    /// </summary>
    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId)
        => _issueDrawerService.IsIssueDistributedAsync(issueIdentifier, issueProviderConfigId);

    // ── Cross-drawer coordination ──

    private void HideOtherDrawers(string keepOpen)
    {
        if (keepOpen != DrawerTabIssue) _issueDrawerService.Hide();
        if (keepOpen != DrawerTabPr) _prReviewDrawerService.Hide();
        if (keepOpen != DrawerTabEpic) _epicDrawerService.Hide();
    }

    /// <summary>Closes whichever drawer is currently open.</summary>
    public void CloseActiveDrawer()
    {
        if (IsIssueDrawerOpen) _issueDrawerService.CloseIssueDrawer();
        else if (IsPrDrawerOpen) _prReviewDrawerService.ClosePrDrawer();
        else if (IsEpicDrawerOpen) _epicDrawerService.CloseEpicDrawer();
    }

    // ── Issue drawer orchestration ──

    public async Task<string?> OpenIssueDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabIssue);
        return await _issueDrawerService.OpenIssueDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public void CloseIssueDrawer() => _issueDrawerService.CloseIssueDrawer();

    public Task<string?> SwitchToIssueDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabIssue);
        return _issueDrawerService.SwitchToIssueDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue)
    {
        var templateId = _issueDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        return _issueDrawerService.DispatchFromIssueDrawerAsync(issue, IssueProviders, RepoProviders, parentProject);
    }

    // ── PR drawer orchestration ──

    // TODO: Contract note — the prior string overload had an explicit `if (string.IsNullOrEmpty(templateId)) return null;`
    // guard that was removed when adopting TemplateId. Callers passing an empty string will throw via
    // ArgumentException.ThrowIfNullOrEmpty. Non-UI callers must use `TemplateId?` and null-check first.
    public async Task<string?> OpenPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabPr);
        // Refresh the active-work-item set before opening, exactly as OpenIssueDrawerAsync does.
        // The PR drawer greys out and badges rows that already have an in-flight work item, and
        // that check reads the same ActiveIssues set the issue drawer maintains — which starts
        // empty on a fresh circuit. Without this refresh the PR drawer never shows a PR as already
        // being processed until something else happens to populate the set.
        await RefreshActiveIssuesAsync();
        return await _prReviewDrawerService.OpenPrDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public void ClosePrDrawer() => _prReviewDrawerService.ClosePrDrawer();

    public async Task<string?> SwitchToPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabPr);
        await RefreshActiveIssuesAsync();
        return await _prReviewDrawerService.SwitchToPrDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(PullRequestSummary pr)
    {
        var templateId = _prReviewDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        return _prReviewDrawerService.DispatchFromPrDrawerAsync(pr, IssueProviders, RepoProviders, parentProject);
    }

    // ── Epic drawer orchestration ──

    // TODO: Same contract note as OpenPrDrawerAsync — passing an empty string will throw.
    public async Task<string?> OpenEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabEpic);
        return await _epicDrawerService.OpenEpicDrawerAsync(templateId, Templates, Projects, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawerService.CloseEpicDrawer();

    public Task<string?> SwitchToEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        PropagateProviderContext();
        HideOtherDrawers(DrawerTabEpic);
        return _epicDrawerService.SwitchToEpicDrawerAsync(templateId, Templates, Projects, notifyStateChanged);
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(IssueSummary issue)
    {
        var templateId = _epicDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        return _epicDrawerService.DispatchFromEpicDrawerAsync(issue, IssueProviders, RepoProviders, parentProject);
    }

    // ── Helpers ──

    public PipelineProject? GetParentProject(TemplateId templateId) =>
        Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId.Value));
}
