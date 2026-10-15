using System.Runtime.CompilerServices;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Coordinator for the AgentCoding page — owns template CRUD, loop controls, provider lists, and
/// forwards drawer operations to the focused drawer services.
/// The Blazor component delegates to this service and retains only UI state.
/// Everything goes through /api/ui with the user's token (spec 051 2.7): the page reads <see cref="IUiConfigClient.GetPipelinesPageAsync"/>
/// (readable projects only, secrets masked), template changes go through the admin settings surface
/// (<see cref="IUiSettingsClient"/>), the loop through <see cref="IUiLoopClient"/>, and dispatch runs in the API (2.3c).
/// </summary>
public class AgentCodingPageService
{
    private readonly IUiLoopClient _loopClient;
    private readonly IUiConfigClient _uiConfig;
    private readonly IUiSettingsClient _settings;
    private readonly IIssueDrawerService _issueDrawerService;
    private readonly IPrReviewDrawerService _prReviewDrawerService;
    private readonly IEpicDrawerService _epicDrawerService;
    private readonly CurrentAccess _access;
    private readonly IAccessGuard _guard;

    public AgentCodingPageService(
        IUiLoopClient loopClient,
        IUiConfigClient uiConfig,
        IUiSettingsClient settings,
        DispatchDrawers drawers,
        CurrentAccess access,
        IAccessGuard guard)
    {
        _access = access;
        _guard = guard;
        _loopClient = loopClient;
        _uiConfig = uiConfig;
        _settings = settings;
        _issueDrawerService = drawers.Issue;
        _prReviewDrawerService = drawers.PrReview;
        _epicDrawerService = drawers.Epic;
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
            await _access.InitializeAsync();
            // Spec 051 2.7: one read with the user's token. The API keeps only the readable projects' templates
            // (a template without a project is global, so only a global reader gets it) and masks secrets.
            var page = await _uiConfig.GetPipelinesPageAsync(CancellationToken.None);
            IssueProviders = page.IssueProviders.ToList();
            PipelineProviders = page.PipelineProviders.ToList();
            BrainProviders = page.RepoProviders.Where(p => p.RepositoryRole == RepositoryRole.Brain).ToList();
            RepoProviders = page.RepoProviders.Where(p => p.RepositoryRole != RepositoryRole.Brain).ToList();
            MaxRetries = page.MaxRetries;
            PipelineConfig = new PipelineConfiguration { DefaultRequiredAgentLabels = page.DefaultRequiredAgentLabels, MaxRetries = page.MaxRetries };
            Projects = page.Projects;
            Templates = page.Templates.ToList();
            QualityGateConfigs = page.QualityGateConfigs;
            ReviewerConfigs = page.ReviewerConfigs;
            AgentProfiles = page.AgentProfiles;

            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to load configuration: {ex.Message}";
        }
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

    public Task<(bool Success, string? Error)> ToggleTriageEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { TriageEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleHousekeepingEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { HousekeepingEnabled = e }, enabled);

    public Task<(bool Success, string? Error)> ToggleBranchCleanupEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { HousekeepingBranchCleanupEnabled = e }, enabled);

    private async Task<(bool Success, string? Error)> TogglePropertyAsync(
        PipelineJobTemplate template,
        Func<PipelineJobTemplate, bool, PipelineJobTemplate> updater,
        bool enabled)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied);
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = updater(template, enabled);
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _settings.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}"); }
        Templates[idx] = updated;
        return (true, null);
    }

    public (bool Valid, string? FormError) ValidateAddTemplate(TemplateTableSection.TemplateFormModel form)
    {
        if (string.IsNullOrWhiteSpace(form.Name)) return (false, "Name is required.");
        if (string.IsNullOrEmpty(form.IssueProviderId)) return (false, "Issue Provider is required.");
        if (string.IsNullOrEmpty(form.RepoProviderId)) return (false, "Repo Provider is required.");

        // A new template is enabled, so it must meet the binding rules the API enforces on save.
        var candidate = new PipelineJobTemplate
        {
            Id = string.Empty,
            Name = form.Name.Trim(),
            IssueProviderId = form.IssueProviderId,
            RepoProviderId = form.RepoProviderId,
            Enabled = true
        };
        var projectId = string.IsNullOrEmpty(form.ProjectId) ? WellKnownIds.DefaultProjectId : form.ProjectId;
        var conflict = TemplateBindingRules.Validate(candidate, projectId, Templates, Projects);
        return conflict is null ? (true, null) : (false, conflict);
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> AddTemplateAsync(TemplateTableSection.TemplateFormModel form)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        var newTemplate = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = form.Name.Trim(),
            IssueProviderId = form.IssueProviderId,
            RepoProviderId = form.RepoProviderId,
            BrainProviderId = string.IsNullOrEmpty(form.BrainProviderId) ? null : form.BrainProviderId,
            PipelineProviderId = string.IsNullOrEmpty(form.PipelineProviderId) ? null : form.PipelineProviderId,
            BrainReadOnly = form.BrainReadOnly,
            ImplementationEnabled = form.ImplementationEnabled,
            ReviewEnabled = form.ReviewEnabled,
            DecompositionEnabled = form.DecompositionEnabled,
            TriageEnabled = form.TriageEnabled,
            HousekeepingEnabled = form.HousekeepingEnabled,
            HousekeepingConcurrencyLimit = form.HousekeepingConcurrencyLimit,
            HousekeepingBranchCleanupEnabled = form.HousekeepingBranchCleanupEnabled,
            Enabled = true
        };
        var targetProjectId = string.IsNullOrEmpty(form.ProjectId) ? WellKnownIds.DefaultProjectId : form.ProjectId;
        try { await _settings.SaveTemplateAsync(targetProjectId, newTemplate, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Templates.Add(newTemplate);
        await LoadProjectsAsync();
        return (true, null, $"Template \"{newTemplate.Name}\" added.");
    }

    /// <summary>
    /// Checks an edit against the rules the API enforces on save. The edited template keeps its enabled state,
    /// repository and issue tracker.
    /// </summary>
    public (bool Valid, string? FormError) ValidateEditTemplate(TemplateTableSection.TemplateFormModel form)
    {
        if (string.IsNullOrWhiteSpace(form.Name)) return (false, "Name is required.");
        var existing = Templates.FirstOrDefault(t => t.Id == form.EditingTemplateId);
        if (existing is null) return (false, "The template no longer exists.");

        var conflict = TemplateBindingRules.Validate(
            ApplyEdit(existing, form), GetParentProject(existing.Id)?.Id ?? WellKnownIds.DefaultProjectId, Templates, Projects);
        return conflict is null ? (true, null) : (false, conflict);
    }

    /// <summary>
    /// Saves an edit of an existing template: name, brain and its read-only switch, CI provider, workflow switches and
    /// housekeeping. The id, project, enabled state, repository and issue tracker stay as they are.
    /// </summary>
    public async Task<(bool Success, string? Error, string? SuccessMessage)> UpdateTemplateAsync(TemplateTableSection.TemplateFormModel form)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        var idx = Templates.FindIndex(t => t.Id == form.EditingTemplateId);
        if (idx < 0) return (false, "The template no longer exists.", null);

        var updated = ApplyEdit(Templates[idx], form);
        var projectId = GetParentProject(updated.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _settings.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Templates[idx] = updated;
        return (true, null, $"Template \"{updated.Name}\" saved.");
    }

    private static PipelineJobTemplate ApplyEdit(PipelineJobTemplate existing, TemplateTableSection.TemplateFormModel form) => existing with
    {
        Name = form.Name.Trim(),
        BrainProviderId = string.IsNullOrEmpty(form.BrainProviderId) ? null : form.BrainProviderId,
        BrainReadOnly = !string.IsNullOrEmpty(form.BrainProviderId) && form.BrainReadOnly,
        PipelineProviderId = string.IsNullOrEmpty(form.PipelineProviderId) ? null : form.PipelineProviderId,
        ImplementationEnabled = form.ImplementationEnabled,
        ReviewEnabled = form.ReviewEnabled,
        DecompositionEnabled = form.DecompositionEnabled,
        TriageEnabled = form.TriageEnabled,
        HousekeepingEnabled = form.HousekeepingEnabled,
        HousekeepingConcurrencyLimit = form.HousekeepingConcurrencyLimit,
        HousekeepingBranchCleanupEnabled = form.HousekeepingBranchCleanupEnabled,
    };

    public async Task<(bool Success, string? Error, string? SuccessMessage)> RemoveTemplateAsync(PipelineJobTemplate template)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _settings.DeleteTemplateAsync(projectId, template.Id, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to delete: {ex.Message}", null); }
        Templates.RemoveAll(t => t.Id == template.Id);
        await LoadProjectsAsync();
        return (true, null, $"Template \"{template.Name}\" removed.");
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> MoveTemplateToProjectAsync(
        TemplateId templateId, string sourceProjectId, string targetProjectId)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        try
        {
            var sourceProject = Projects.FirstOrDefault(p => p.Id == sourceProjectId);
            var targetProject = Projects.FirstOrDefault(p => p.Id == targetProjectId);
            if (sourceProject == null || targetProject == null) return (true, null, null);
            // The template's own project is the only membership record, so a move is one call.
            await _settings.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId.Value, CancellationToken.None);
            await LoadProjectsAsync();
            return (true, null, $"Moved \"{Templates.FirstOrDefault(t => t.Id == templateId.Value)?.Name ?? templateId.Value}\" to {targetProject.Name}.");
        }
        catch (Exception ex) { return (false, $"Failed to move template: {ex.Message}", null); }
    }

    // ── Loop Controls ──
    // Spec 051 D11: each command only records the request; the Scheduler leader applies it within a
    // few seconds. A start it refuses shows up in the loop status's validation errors.

    public Task<(bool Success, string? Error)> StartLoopAsync() =>
        SendLoopCommandAsync(_loopClient.StartAsync, "start");

    public Task<(bool Success, string? Error)> StopLoopAsync() =>
        SendLoopCommandAsync(_loopClient.StopAsync, "stop");

    public Task<(bool Success, string? Error)> ResumeLoopAsync() =>
        SendLoopCommandAsync(_loopClient.ResumeAsync, "resume");

    private async Task<(bool Success, string? Error)> SendLoopCommandAsync(Func<CancellationToken, Task> send, string verb)
    {
        if (await DeniedAsync(AccessRole.Operator) is { } denied) return (false, denied);
        try
        {
            await send(CancellationToken.None);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to {verb} loop: {ex.Message}");
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

    /// <summary>Reloads the Queued/Running badges for the template's trackers and repository.</summary>
    public Task RefreshActiveIssuesAsync(TemplateId templateId) => _issueDrawerService.RefreshActiveIssuesAsync(templateId, CancellationToken.None);

    public bool IsIssueActive(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => _issueDrawerService.IsIssueActive(issueIdentifier, issueProviderConfigId);

    /// <summary>
    /// Returns the WorkItemStatus of the issue's current work item, or null if not active.
    /// <see cref="WorkItemStatus.Pending"/> = Queued; <see cref="WorkItemStatus.Running"/> = Running/Dispatched.
    /// </summary>
    public WorkItemStatus? GetIssueWorkItemStatus(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => _issueDrawerService.GetIssueWorkItemStatus(issueIdentifier, issueProviderConfigId);

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
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabIssue);
        return await _issueDrawerService.OpenIssueDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public void CloseIssueDrawer() => _issueDrawerService.CloseIssueDrawer();

    public async Task<string?> SwitchToIssueDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabIssue);
        return await _issueDrawerService.SwitchToIssueDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue)
    {
        var templateId = _issueDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        if (await DeniedAsync(AccessRole.Operator, parentProject?.Id) is { } denied) return (false, denied, null);
        return await _issueDrawerService.DispatchFromIssueDrawerAsync(issue);
    }

    // ── PR drawer orchestration ──

    // TODO: Contract note — the prior string overload had an explicit `if (string.IsNullOrEmpty(templateId)) return null;`
    // guard that was removed when adopting TemplateId. Callers passing an empty string will throw via
    // ArgumentException.ThrowIfNullOrEmpty. Non-UI callers must use `TemplateId?` and null-check first.
    public async Task<string?> OpenPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabPr);
        // Refresh the active-work-item set before opening, exactly as OpenIssueDrawerAsync does.
        // The PR drawer greys out and badges rows that already have an in-flight work item, and
        // that check reads the same ActiveIssues set the issue drawer maintains — which starts
        // empty on a fresh circuit. Without this refresh the PR drawer never shows a PR as already
        // being processed until something else happens to populate the set.
        await RefreshActiveIssuesAsync(templateId);
        return await _prReviewDrawerService.OpenPrDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public void ClosePrDrawer() => _prReviewDrawerService.ClosePrDrawer();

    public async Task<string?> SwitchToPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabPr);
        await RefreshActiveIssuesAsync(templateId);
        return await _prReviewDrawerService.SwitchToPrDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(PullRequestSummary pr)
    {
        var templateId = _prReviewDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        if (await DeniedAsync(AccessRole.Operator, parentProject?.Id) is { } denied) return (false, denied, null);
        return await _prReviewDrawerService.DispatchFromPrDrawerAsync(pr);
    }

    // ── Epic drawer orchestration ──

    // TODO: Same contract note as OpenPrDrawerAsync — passing an empty string will throw.
    public async Task<string?> OpenEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabEpic);
        return await _epicDrawerService.OpenEpicDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawerService.CloseEpicDrawer();

    public async Task<string?> SwitchToEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        if (await DeniedForTemplateAsync(AccessRole.ReadOnly, templateId) is { } denied) return denied;
        HideOtherDrawers(DrawerTabEpic);
        return await _epicDrawerService.SwitchToEpicDrawerAsync(templateId, Templates, notifyStateChanged);
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(IssueSummary issue)
    {
        var templateId = _epicDrawerService.DrawerState.Template?.Id;
        var parentProject = string.IsNullOrEmpty(templateId) ? null : GetParentProject(templateId);
        if (await DeniedAsync(AccessRole.Operator, parentProject?.Id) is { } denied) return (false, denied, null);
        return await _epicDrawerService.DispatchFromEpicDrawerAsync(issue);
    }

    // ── Helpers ──

    /// <summary>Only readable projects are kept, so a template of any other project has no parent here.</summary>
    public PipelineProject? GetParentProject(TemplateId templateId) =>
        Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId.Value));

    /// <summary>Whether the signed-in user may dispatch work of this template (operator on its project).</summary>
    public bool CanDispatch(TemplateId templateId) => _access.CanOperate(GetParentProject(templateId)?.Id);

    private async Task LoadProjectsAsync() =>
        Projects = (await _uiConfig.GetPipelinesPageAsync(CancellationToken.None)).Projects;

    /// <summary>Spec 049 Req 7.3: the denial message, or null when the user holds <paramref name="role"/>.</summary>
    private async Task<string?> DeniedAsync(AccessRole role, string? projectId = null, [CallerMemberName] string action = "")
    {
        try
        {
            await _guard.DemandAsync(role, projectId, action);
            return null;
        }
        catch (AccessDeniedException)
        {
            return AccessDeniedException.UserMessage;
        }
    }

    /// <summary>
    /// The template ID comes from the browser (Req 7.4). A template of an unreadable project has no
    /// parent in <see cref="Projects"/>, so the check falls back to the global role and fails for scoped users.
    /// </summary>
    private Task<string?> DeniedForTemplateAsync(AccessRole role, TemplateId templateId, [CallerMemberName] string action = "") =>
        DeniedAsync(role, GetParentProject(templateId)?.Id, action);
}

/// <summary>The Pipelines page's three dispatch drawers (issues, pull requests, epics), resolved together.</summary>
public sealed record DispatchDrawers(IIssueDrawerService Issue, IPrReviewDrawerService PrReview, IEpicDrawerService Epic);
