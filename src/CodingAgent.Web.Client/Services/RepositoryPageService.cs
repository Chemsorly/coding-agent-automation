using System.Runtime.CompilerServices;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.Services;

/// <summary>
/// Coordinator for the RepositoryPage — owns template load, per-card save (Binding and Behaviour),
/// and forwards drawer operations to the focused drawer services.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public class RepositoryPageService
{
    private const string TemplateNotFoundMessage = "Template not found.";

    private readonly IUiConfigClient _uiConfig;
    private readonly IUiSettingsClient _settings;
    private readonly IUiConsolidationClient _consolidation;
    private readonly IIssueDrawerService _issueDrawerService;
    private readonly IPrReviewDrawerService _prReviewDrawerService;
    private readonly IEpicDrawerService _epicDrawerService;
    private readonly DrawerStatePresenter _drawerPresenter;
    private readonly CurrentAccess _access;
    private readonly IAccessGuard _guard;

    public RepositoryPageService(
        IUiConfigClient uiConfig,
        IUiSettingsClient settings,
        IUiConsolidationClient consolidation,
        DispatchDrawers drawers,
        CurrentAccess access,
        IAccessGuard guard)
    {
        _uiConfig = uiConfig;
        _settings = settings;
        _consolidation = consolidation;
        _issueDrawerService = drawers.Issue;
        _prReviewDrawerService = drawers.PrReview;
        _epicDrawerService = drawers.Epic;
        _drawerPresenter = new DrawerStatePresenter(drawers);
        _access = access;
        _guard = guard;
    }

    // ── Page data state ──

    public PipelineJobTemplate? Template { get; private set; }
    public PipelineProject? Project { get; private set; }
    // NOTE: IssueProviders, RepoProviders, PipelineProviders and BrainProviders are List<T> rather than
    // IReadOnlyList<T>, inconsistent with QualityGateConfigs, ReviewerConfigs, AgentProfiles, AllProjects and
    // AllTemplates below. Callers can mutate the list contents via .Add()/.Remove() despite the private setter.
    // Change these four to IReadOnlyList<ProviderConfig> and assign .AsReadOnly() in InitializeAsync to match
    // the existing pattern. (DotNetSpecialist review finding, issue #3576)
    public List<ProviderConfig> IssueProviders { get; private set; } = [];
    public List<ProviderConfig> RepoProviders { get; private set; } = [];
    public List<ProviderConfig> PipelineProviders { get; private set; } = [];
    public List<ProviderConfig> BrainProviders { get; private set; } = [];
    public IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; private set; } = [];
    public IReadOnlyList<ReviewerConfiguration> ReviewerConfigs { get; private set; } = [];
    public IReadOnlyList<AgentProfile> AgentProfiles { get; private set; } = [];
    public PipelineConfiguration PipelineConfig { get; private set; } = new();
    public IReadOnlyList<PipelineProject> AllProjects { get; private set; } = [];
    public IReadOnlyList<PipelineJobTemplate> AllTemplates { get; private set; } = [];

    // ── Drawer state accessors ──

    public bool IsIssueDrawerOpen => _drawerPresenter.IsIssueDrawerOpen;
    public bool IsPrDrawerOpen => _drawerPresenter.IsPrDrawerOpen;
    public bool IsEpicDrawerOpen => _drawerPresenter.IsEpicDrawerOpen;
    public PipelineJobTemplate? IssueDrawerTemplate => _drawerPresenter.IssueDrawerTemplate;
    public PipelineJobTemplate? PrDrawerTemplate => _drawerPresenter.PrDrawerTemplate;
    public PipelineJobTemplate? EpicDrawerTemplate => _drawerPresenter.EpicDrawerTemplate;
    public bool IssueDrawerDispatching { get => _drawerPresenter.IssueDrawerDispatching; set => _drawerPresenter.IssueDrawerDispatching = value; }
    public bool PrDrawerDispatching { get => _drawerPresenter.PrDrawerDispatching; set => _drawerPresenter.PrDrawerDispatching = value; }
    public bool EpicDrawerDispatching { get => _drawerPresenter.EpicDrawerDispatching; set => _drawerPresenter.EpicDrawerDispatching = value; }

    public List<IssueSummary> DrawerIssues => _drawerPresenter.DrawerIssues;
    public int DrawerPage => _drawerPresenter.DrawerPage;
    public bool DrawerHasMore => _drawerPresenter.DrawerHasMore;
    public bool DrawerLoading => _drawerPresenter.DrawerLoading;
    public List<string> DrawerLabels => _drawerPresenter.DrawerLabels;
    public List<string> DrawerSelectedLabels => _drawerPresenter.DrawerSelectedLabels;
    public Dictionary<string, DependencyCheckResult> DrawerReadiness => _drawerPresenter.DrawerReadiness;

    public List<PullRequestSummary> PrDrawerPrs => _drawerPresenter.PrDrawerPrs;
    public int PrDrawerPage => _drawerPresenter.PrDrawerPage;
    public bool PrDrawerHasMore => _drawerPresenter.PrDrawerHasMore;
    public bool PrDrawerLoading => _drawerPresenter.PrDrawerLoading;
    public List<string> PrDrawerLabels => _drawerPresenter.PrDrawerLabels;
    public List<string> PrDrawerSelectedLabels => _drawerPresenter.PrDrawerSelectedLabels;

    public List<IssueSummary> EpicDrawerIssues => _drawerPresenter.EpicDrawerIssues;
    public int EpicDrawerPage => _drawerPresenter.EpicDrawerPage;
    public bool EpicDrawerHasMore => _drawerPresenter.EpicDrawerHasMore;
    public bool EpicDrawerLoading => _drawerPresenter.EpicDrawerLoading;
    public List<string> EpicDrawerLabels => _drawerPresenter.EpicDrawerLabels;
    public List<string> EpicDrawerSelectedLabels => _drawerPresenter.EpicDrawerSelectedLabels;

    // ── Initialization ──

    /// <summary>
    /// Loads the page for the given template ID. Returns null on success; returns an error string on API failure.
    /// Call <see cref="CanView"/> after this to check whether the template is accessible.
    /// </summary>
    public async Task<string?> InitializeAsync(string templateId)
    {
        try
        {
            await _access.InitializeAsync();
            var page = await _uiConfig.GetPipelinesPageAsync(CancellationToken.None);
            IssueProviders = page.IssueProviders.ToList();
            PipelineProviders = page.PipelineProviders.ToList();
            BrainProviders = page.RepoProviders.Where(p => p.RepositoryRole == RepositoryRole.Brain).ToList();
            RepoProviders = page.RepoProviders.Where(p => p.RepositoryRole != RepositoryRole.Brain).ToList();
            QualityGateConfigs = page.QualityGateConfigs;
            ReviewerConfigs = page.ReviewerConfigs;
            AgentProfiles = page.AgentProfiles;
            PipelineConfig = new PipelineConfiguration
            {
                DefaultRequiredAgentLabels = page.DefaultRequiredAgentLabels,
                MaxRetries = page.MaxRetries
            };
            AllProjects = page.Projects;
            AllTemplates = page.Templates;

            Template = page.Templates.FirstOrDefault(t => t.Id == templateId);
            if (Template is not null)
            {
                Project = page.Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId));
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to load configuration: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true when the current user may view the template.
    /// False for unknown template IDs and for templates whose project the user cannot read —
    /// both cases show the same not-found state to prevent information disclosure.
    /// </summary>
    public bool CanView()
    {
        if (Template is null) return false;
        var projectId = Project?.Id;
        return _access.CanRead(projectId);
    }

    /// <summary>Spec 049: the user may dispatch only when they are an operator on the template's project.</summary>
    public bool CanDispatch() => _access.CanOperate(Project?.Id);

    // ── Repository web URL ──

    /// <summary>
    /// Constructs the web URL for the repository from the provider's Settings dictionary.
    /// Returns null when the required settings keys are absent or the provider type is not recognised.
    /// Never throws.
    /// </summary>
    public static string? GetRepositoryWebUrl(ProviderConfig config)
    {
        try
        {
            var settings = config.Settings;
            if (string.Equals(config.ProviderType, ProviderTypes.GitHub, StringComparison.OrdinalIgnoreCase))
            {
                settings.TryGetValue(ProviderSettingKeys.Owner, out var owner);
                settings.TryGetValue(ProviderSettingKeys.Repo, out var repo);
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) return null;

                // For GitHub Enterprise, derive the web base from apiUrl.
                settings.TryGetValue(ProviderSettingKeys.ApiUrl, out var apiUrl);
                if (!string.IsNullOrEmpty(apiUrl)
                    && !string.Equals(apiUrl, ProviderSettingKeys.DefaultApiUrl, StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri))
                {
                    // apiUrl is typically https://github.example.com/api/v3 for GHE.
                    // Web base is the scheme+host, e.g. https://github.example.com.
                    // NOTE: apiUri.Host is used here rather than apiUri.Authority (host + port).
                    // For a GHE instance at https://ghe.example.com:8443/api/v3 this omits port 8443
                    // and produces the wrong link. Change Host to Authority to fix GHE on non-default
                    // ports. (Correctness review finding, issue #3576)
                    var webBase = $"{apiUri.Scheme}://{apiUri.Host}";
                    return $"{webBase}/{owner}/{repo}";
                }

                return $"https://github.com/{owner}/{repo}";
            }

            if (string.Equals(config.ProviderType, ProviderTypes.GitLab, StringComparison.OrdinalIgnoreCase))
            {
                settings.TryGetValue(ProviderSettingKeys.ApiUrl, out var apiUrl);
                settings.TryGetValue(ProviderSettingKeys.ProjectId, out var projectId);
                // GitLab web URLs require the project's full path, not its numeric id.
                // We cannot reconstruct a path from an id without an API call, so return null.
                // The provider's DisplayName is already shown; the link is optional.
                _ = apiUrl;
                _ = projectId;
                return null;
            }
        }
        catch (Exception)
        {
            // Missing keys or unexpected format: silently suppress the link.
        }

        return null;
    }

    // ── Label preview ──

    /// <summary>Resolves the label preview for the given required labels, using the current page data.</summary>
    public TemplateTableSection.LabelPreviewResult GetLabelPreview(
        IReadOnlyList<string>? requiredLabels,
        IReadOnlyList<AgentEntryDto> agents)
    {
        IReadOnlyList<string> labels;
        if (requiredLabels is { Count: > 0 })
        {
            labels = requiredLabels;
        }
        else if (!string.IsNullOrWhiteSpace(PipelineConfig?.DefaultRequiredAgentLabels))
        {
            labels = PipelineConfig.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList().AsReadOnly();
        }
        else
            return new TemplateTableSection.LabelPreviewResult();

        if (labels.Count == 0)
            return new TemplateTableSection.LabelPreviewResult();

        var matchedProfile = ProfileResolver.ResolveByRequiredLabels(AgentProfiles, labels);
        var resolveLabels = matchedProfile?.MatchLabels ?? labels;

        var qgResolver = new QualityGateResolver();
        var matchedQgs = qgResolver.Resolve(QualityGateConfigs, resolveLabels);

        var rvResolver = new ReviewerResolver();
        var matchedRvs = rvResolver.Resolve(ReviewerConfigs, resolveLabels);

        var allAgents = agents
            .Where(a => !a.Disabled)
            .Where(a => labels.Count == 0 || labels.All(l => a.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var onlineCount = allAgents.Count(a => a.Status != AgentStatus.Disconnected);

        return new TemplateTableSection.LabelPreviewResult
        {
            Labels = labels,
            QualityGates = matchedQgs.Select(q => q.DisplayName).ToList(),
            Reviewers = matchedRvs.Select(r => $"{r.DisplayName} ({r.Agents.Count} agent{(r.Agents.Count != 1 ? "s" : "")})").ToList(),
            Profiles = matchedProfile is not null ? [matchedProfile.DisplayName] : [],
            Agents = allAgents.Select(a => a.AgentId.Value).ToList(),
            OnlineAgentCount = onlineCount
        };
    }

    // ── Save operations ──

    /// <summary>Saves only the Binding fields of the template (brain, CI provider, labels, enabled).</summary>
    public async Task<(bool Success, string? Error, string? SuccessMessage)> SaveBindingAsync(
        BindingFormModel form)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        if (Template is null || Project is null) return (false, TemplateNotFoundMessage, null);

        var updated = ApplyBindingEdit(Template, form);
        var conflict = TemplateBindingRules.Validate(updated, Project.Id, AllTemplates.ToList(), AllProjects.ToList());
        if (conflict is not null) return (false, conflict, null);

        try { await _settings.SaveTemplateAsync(Project.Id, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Template = updated;
        return (true, null, $"Binding saved. Changes apply from the next poll cycle.");
    }

    /// <summary>Saves only the Behaviour fields of the template (name, switches, housekeeping, repo settings).</summary>
    public async Task<(bool Success, string? Error, string? SuccessMessage)> SaveBehaviourAsync(
        BehaviourFormModel form)
    {
        if (await DeniedAsync(AccessRole.Admin) is { } denied) return (false, denied, null);
        if (Template is null || Project is null) return (false, TemplateNotFoundMessage, null);

        if (string.IsNullOrWhiteSpace(form.Name)) return (false, "Name is required.", null);

        var updated = ApplyBehaviourEdit(Template, form);
        var conflict = TemplateBindingRules.Validate(updated, Project.Id, AllTemplates.ToList(), AllProjects.ToList());
        if (conflict is not null) return (false, conflict, null);

        try { await _settings.SaveTemplateAsync(Project.Id, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Template = updated;
        return (true, null, $"Behaviour saved. Changes apply from the next poll cycle.");
    }

    // ── Apply helpers ──

    private static PipelineJobTemplate ApplyBindingEdit(PipelineJobTemplate existing, BindingFormModel form) =>
        existing with
        {
            BrainProviderId = string.IsNullOrEmpty(form.BrainProviderId) ? null : form.BrainProviderId,
            BrainReadOnly = !string.IsNullOrEmpty(form.BrainProviderId) && form.BrainReadOnly,
            PipelineProviderId = string.IsNullOrEmpty(form.PipelineProviderId) ? null : form.PipelineProviderId,
            RequiredLabels = form.RequiredLabels is { Count: > 0 } ? form.RequiredLabels : null,
            Enabled = form.Enabled,
        };

    private static PipelineJobTemplate ApplyBehaviourEdit(PipelineJobTemplate existing, BehaviourFormModel form)
    {
        // Build the new secrets: for write-only mode, keep existing entries that weren't replaced.
        // BehaviourFormModel.Secrets already contains the merged result from RepositorySettingsEditor.
        var newSecrets = form.Secrets is { Count: > 0 } ? new Dictionary<string, string>(form.Secrets) : null;

        return existing with
        {
            Name = form.Name.Trim(),
            ImplementationEnabled = form.ImplementationEnabled,
            ReviewEnabled = form.ReviewEnabled,
            DecompositionEnabled = form.DecompositionEnabled,
            HousekeepingEnabled = form.HousekeepingEnabled,
            HousekeepingConcurrencyLimit = form.HousekeepingConcurrencyLimit,
            HousekeepingBranchCleanupEnabled = form.HousekeepingBranchCleanupEnabled,
            RequiredLabels = existing.RequiredLabels, // Preserved — owned by Binding card
            Secrets = newSecrets,
            SetupSteps = form.SetupSteps is { Count: > 0 } ? form.SetupSteps : null,
            SteeringContent = !string.IsNullOrEmpty(form.SteeringContent) ? form.SteeringContent : null,
            BlacklistedPaths = form.BlacklistedPaths is { Count: > 0 } ? form.BlacklistedPaths : null,
        };
    }

    // ── Consolidation trigger ──

    public async Task<string?> TriggerRefactoringScanAsync(string templateId, bool autoDispatch, RefactoringScanFocus focus)
    {
        // NOTE: DeniedAsync(AccessRole.Operator) passes Project?.Id to _guard.DemandAsync, making this a
        // project-scoped operator check. The UI gate (Access.HasGlobalOperate) and the issue requirement both
        // specify a global operator. A project-level operator (not a global operator) cannot see the button in
        // the UI, but this service-level check would still permit the trigger if invoked outside the component.
        // Change to DemandAsync(AccessRole.Operator, null, action) for global-scope enforcement matching the
        // Consolidation page behaviour. (Correctness review finding, issue #3576)
        if (await DeniedAsync(AccessRole.Operator) is { } denied) return denied;
        try
        {
            await _consolidation.TriggerAsync(ConsolidationRunType.RefactoringDetection, templateId, autoDispatch, focus: focus);
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        catch (Exception ex) { return $"Refactoring scan dispatch failed: {ex.Message}"; }
    }

    public async Task<UiRefactoringScanSettings?> GetRefactoringScanSettingsAsync(string templateId)
    {
        try { return await _consolidation.GetRefactoringScanSettingsAsync(templateId); }
        catch { return null; }
    }

    // ── Drawer orchestration (forwarded to drawer services) ──

    public Task<string?> OpenIssueDrawerAsync(Func<Task> notifyStateChanged)
    {
        if (Template is null) return Task.FromResult<string?>(TemplateNotFoundMessage);
        return _issueDrawerService.OpenIssueDrawerAsync(
            new TemplateId(Template.Id), AllTemplates, notifyStateChanged);
    }

    public Task<string?> OpenPrDrawerAsync(Func<Task> notifyStateChanged)
    {
        if (Template is null) return Task.FromResult<string?>(TemplateNotFoundMessage);
        return _prReviewDrawerService.OpenPrDrawerAsync(
            new TemplateId(Template.Id), AllTemplates, notifyStateChanged);
    }

    public Task<string?> OpenEpicDrawerAsync(Func<Task> notifyStateChanged)
    {
        if (Template is null) return Task.FromResult<string?>(TemplateNotFoundMessage);
        return _epicDrawerService.OpenEpicDrawerAsync(
            new TemplateId(Template.Id), AllTemplates, notifyStateChanged);
    }

    public void CloseIssueDrawer() => _issueDrawerService.CloseIssueDrawer();
    public void ClosePrDrawer() => _prReviewDrawerService.ClosePrDrawer();
    public void CloseEpicDrawer() => _epicDrawerService.CloseEpicDrawer();

    public Task<string?> DrawerNextPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _issueDrawerService.LoadDrawerIssuesPageAsync(Template, DrawerPage + 1);

    public Task<string?> DrawerPrevPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _issueDrawerService.LoadDrawerIssuesPageAsync(Template, Math.Max(1, DrawerPage - 1));

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue)
        => _issueDrawerService.DispatchFromIssueDrawerAsync(issue);

    public Task<string?> PrDrawerNextPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _prReviewDrawerService.LoadPrDrawerPageAsync(Template, PrDrawerPage + 1);

    public Task<string?> PrDrawerPrevPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _prReviewDrawerService.LoadPrDrawerPageAsync(Template, Math.Max(1, PrDrawerPage - 1));

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(PullRequestSummary pr)
        => _prReviewDrawerService.DispatchFromPrDrawerAsync(pr);

    public Task<string?> EpicDrawerNextPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _epicDrawerService.LoadEpicDrawerIssuesAsync(Template, EpicDrawerPage + 1);

    public Task<string?> EpicDrawerPrevPageAsync() =>
        Template is null
            ? Task.FromResult<string?>(null)
            : _epicDrawerService.LoadEpicDrawerIssuesAsync(Template, Math.Max(1, EpicDrawerPage - 1));

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(IssueSummary issue)
        => _epicDrawerService.DispatchFromEpicDrawerAsync(issue);

    public void DrawerToggleLabel(string label) => _issueDrawerService.DrawerState.ToggleLabel(label);
    public void DrawerClearLabels() => _issueDrawerService.DrawerState.ClearLabelFilter();
    public void PrDrawerToggleLabel(string label) => _prReviewDrawerService.DrawerState.ToggleLabel(label);
    public void PrDrawerClearLabels() => _prReviewDrawerService.DrawerState.ClearLabelFilter();
    public void EpicDrawerToggleLabel(string label) => _epicDrawerService.DrawerState.ToggleLabel(label);
    public void EpicDrawerClearLabels() => _epicDrawerService.DrawerState.ClearLabelFilter();

    // ── Helpers ──

    private async Task<string?> DeniedAsync(AccessRole required, [CallerMemberName] string action = "")
    {
        try
        {
            await _guard.DemandAsync(required, Project?.Id, action);
            return null;
        }
        catch (AccessDeniedException)
        {
            return AccessDeniedException.UserMessage;
        }
    }

    // ── Form models ──

    /// <summary>Form model for the Binding card (brain, CI, labels, enabled).</summary>
    public sealed class BindingFormModel
    {
        public string BrainProviderId { get; set; } = "";
        public bool BrainReadOnly { get; set; }
        public string PipelineProviderId { get; set; } = "";
        public IReadOnlyList<string>? RequiredLabels { get; set; }
        public bool Enabled { get; set; } = true;

        public static BindingFormModel FromTemplate(PipelineJobTemplate t) => new()
        {
            BrainProviderId = t.BrainProviderId ?? "",
            BrainReadOnly = t.BrainReadOnly,
            PipelineProviderId = t.PipelineProviderId ?? "",
            RequiredLabels = t.RequiredLabels,
            Enabled = t.Enabled,
        };
    }

    /// <summary>Form model for the Behaviour card (name, switches, housekeeping, repo settings).</summary>
    public sealed class BehaviourFormModel
    {
        public string Name { get; set; } = "";
        public bool ImplementationEnabled { get; set; } = true;
        public bool ReviewEnabled { get; set; } = true;
        public bool DecompositionEnabled { get; set; }
        public bool HousekeepingEnabled { get; set; }
        public int? HousekeepingConcurrencyLimit { get; set; }
        public bool HousekeepingBranchCleanupEnabled { get; set; }
        public Dictionary<string, string>? Secrets { get; set; }
        public IReadOnlyList<SetupStep>? SetupSteps { get; set; }
        public string? SteeringContent { get; set; }
        public IReadOnlyList<string>? BlacklistedPaths { get; set; }

        public static BehaviourFormModel FromTemplate(PipelineJobTemplate t) => new()
        {
            Name = t.Name,
            ImplementationEnabled = t.ImplementationEnabled,
            ReviewEnabled = t.ReviewEnabled,
            DecompositionEnabled = t.DecompositionEnabled,
            HousekeepingEnabled = t.HousekeepingEnabled,
            HousekeepingConcurrencyLimit = t.HousekeepingConcurrencyLimit,
            HousekeepingBranchCleanupEnabled = t.HousekeepingBranchCleanupEnabled,
            Secrets = t.Secrets != null ? new Dictionary<string, string>(t.Secrets) : null,
            SetupSteps = t.SetupSteps,
            SteeringContent = t.SteeringContent,
            BlacklistedPaths = t.BlacklistedPaths,
        };
    }
}
