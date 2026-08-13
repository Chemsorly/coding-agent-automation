using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Encapsulates the business logic for the AgentCoding page — template CRUD, drawer operations,
/// loop controls, and dispatch coordination. The Blazor component delegates to this service
/// and retains only UI state (visibility, timers, JS interop, StateHasChanged).
/// Registered as Scoped because it holds per-page mutable state.
/// </summary>
public class AgentCodingPageService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AgentCodingPageService>();

    private readonly IPipelineLoopService _loopService;
    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentRegistryService _agentRegistry;
    private readonly IConfigurationStore _configStore;
    private readonly IProjectStore _projectStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IDependencyChecker _dependencyChecker;
    private readonly IDispatchOrchestrationService? _dispatchOrchestration;

    public AgentCodingPageService(
        IPipelineLoopService loopService,
        IWorkDistributor workDistributor,
        IAgentRegistryService agentRegistry,
        IConfigurationStore configStore,
        IProjectStore projectStore,
        IProviderFactory providerFactory,
        IDependencyChecker dependencyChecker,
        IDispatchOrchestrationService? dispatchOrchestration = null)
    {
        _loopService = loopService;
        _workDistributor = workDistributor;
        _agentRegistry = agentRegistry;
        _configStore = configStore;
        _projectStore = projectStore;
        _providerFactory = providerFactory;
        _dependencyChecker = dependencyChecker;
        _dispatchOrchestration = dispatchOrchestration;

        _issueDrawer = new DrawerStateService<IssueSummary>(
            LoadDrawerIssuesAsync,
            LoadDrawerLabelsPrivateAsync,
            (issue, template) => DispatchIssueAsync(issue, template),
            closeOnDispatch: true,
            postLoadAsync: CheckDrawerDependenciesInBackgroundAsync);

        _prDrawer = new DrawerStateService<PullRequestSummary>(
            LoadPrDrawerPageAsync,
            LoadPrDrawerLabelsAsync,
            (pr, template) => DispatchPrReviewAsync(pr, template));

        _epicDrawer = new DrawerStateService<IssueSummary>(
            t => LoadEpicDrawerIssuesAsync(t, 1),
            LoadEpicDrawerLabelsAsync,
            (issue, template) => DispatchDecompositionAsync(issue, template),
            closeOnDispatch: true);
    }

    // ── State ──

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

    // Drawer state — managed by DrawerStateService<TItem> instances
    private readonly DrawerStateService<IssueSummary> _issueDrawer;
    private readonly DrawerStateService<PullRequestSummary> _prDrawer;
    private readonly DrawerStateService<IssueSummary> _epicDrawer;

    // ── Backward-compatible drawer accessors ──

    public DrawerStateService<IssueSummary> IssueDrawer => _issueDrawer;
    public DrawerStateService<PullRequestSummary> PrDrawer => _prDrawer;
    public DrawerStateService<IssueSummary> EpicDrawer => _epicDrawer;

    // Backward-compatible wrappers for existing consumers
    public bool IsIssueDrawerOpen => _issueDrawer.IsOpen;
    public bool IsPrDrawerOpen => _prDrawer.IsOpen;
    public bool IsEpicDrawerOpen => _epicDrawer.IsOpen;
    public PipelineJobTemplate? IssueDrawerTemplate => _issueDrawer.Template;
    public PipelineJobTemplate? PrDrawerTemplate => _prDrawer.Template;
    public PipelineJobTemplate? EpicDrawerTemplate => _epicDrawer.Template;
    public bool IssueDrawerDispatching { get => _issueDrawer.IsDispatching; set => _issueDrawer.IsDispatching = value; }
    public bool PrDrawerDispatching { get => _prDrawer.IsDispatching; set => _prDrawer.IsDispatching = value; }
    public bool EpicDrawerDispatching { get => _epicDrawer.IsDispatching; set => _epicDrawer.IsDispatching = value; }
    public List<IssueSummary> DrawerIssues => _issueDrawer.Items;
    public int DrawerPage => _issueDrawer.Page;
    public bool DrawerHasMore => _issueDrawer.HasMore;
    public bool DrawerLoading => _issueDrawer.Loading;
    public List<string> DrawerLabels => _issueDrawer.Labels;
    public List<string> DrawerSelectedLabels => _issueDrawer.SelectedLabels;
    public List<PullRequestSummary> PrDrawerPrs => _prDrawer.Items;
    public int PrDrawerPage => _prDrawer.Page;
    public bool PrDrawerHasMore => _prDrawer.HasMore;
    public bool PrDrawerLoading => _prDrawer.Loading;
    public List<string> PrDrawerLabels => _prDrawer.Labels;
    public List<string> PrDrawerSelectedLabels => _prDrawer.SelectedLabels;
    public List<IssueSummary> EpicDrawerIssues => _epicDrawer.Items;
    public int EpicDrawerPage => _epicDrawer.Page;
    public bool EpicDrawerHasMore => _epicDrawer.HasMore;
    public bool EpicDrawerLoading => _epicDrawer.Loading;
    public List<string> EpicDrawerLabels => _epicDrawer.Labels;
    public List<string> EpicDrawerSelectedLabels => _epicDrawer.SelectedLabels;
    public Dictionary<string, DependencyCheckResult> DrawerReadiness { get; private set; } = new();

    public HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssues { get; private set; } = new();

    private const string DrawerTabIssue = "issue";
    private const string DrawerTabPr = "pr";
    private const string DrawerTabEpic = "epic";
    private const string InitiatedByManual = "manual";

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

    // TODO: DrawerCancellationToken now returns only the issue drawer's CTS. Consumers using this for
    // non-issue drawer operations will get the wrong token. Consider removing and directing callers
    // to the specific DrawerStateService instance's CancellationToken.
    public CancellationToken DrawerCancellationToken => _issueDrawer.CancellationToken;

    // ── Initialization ──

    public async Task<string?> InitializeAsync()
    {
        try
        {
            IssueProviders = (await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None)).ToList();
            var allRepoProviders = (await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None)).ToList();
            PipelineProviders = (await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None)).ToList();
            BrainProviders = allRepoProviders.Where(p => p.RepositoryRole == RepositoryRole.Brain).ToList();
            RepoProviders = allRepoProviders.Where(p => p.RepositoryRole != RepositoryRole.Brain).ToList();

            var config = await _configStore.LoadPipelineConfigAsync(CancellationToken.None);
            MaxRetries = config.MaxRetries;
            Templates = (await _projectStore.LoadAllTemplatesAsync(CancellationToken.None)).ToList();
            PipelineConfig = config;
            Projects = await _projectStore.LoadProjectsAsync(CancellationToken.None);
            QualityGateConfigs = await _configStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            ReviewerConfigs = await _configStore.LoadReviewerConfigsAsync(CancellationToken.None);
            AgentProfiles = await _configStore.LoadAgentProfilesAsync(CancellationToken.None);
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

    public Task<(bool Success, string? Error)> ToggleHousekeepingEnabledAsync(PipelineJobTemplate template, bool enabled)
        => TogglePropertyAsync(template, (t, e) => t with { HousekeepingEnabled = e }, enabled);

    private async Task<(bool Success, string? Error)> TogglePropertyAsync(
        PipelineJobTemplate template,
        Func<PipelineJobTemplate, bool, PipelineJobTemplate> updater,
        bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = updater(template, enabled);
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _projectStore.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
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
            Enabled = true
        };
        var targetProjectId = string.IsNullOrEmpty(form.ProjectId) ? WellKnownIds.DefaultProjectId : form.ProjectId;
        try { await _projectStore.SaveTemplateAsync(targetProjectId, newTemplate, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}", null); }
        Templates.Add(newTemplate);
        Projects = await _projectStore.LoadProjectsAsync(CancellationToken.None);
        return (true, null, $"Template \"{newTemplate.Name}\" added.");
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> RemoveTemplateAsync(PipelineJobTemplate template)
    {
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _projectStore.DeleteTemplateAsync(projectId, template.Id, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to delete: {ex.Message}", null); }
        Templates.RemoveAll(t => t.Id == template.Id);
        Projects = await _projectStore.LoadProjectsAsync(CancellationToken.None);
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
            await _projectStore.SaveProjectAsync(sourceProject with { TemplateIds = sourceProject.TemplateIds.Where(id => id != templateId.Value).ToList() }, CancellationToken.None);
            await _projectStore.SaveProjectAsync(targetProject with { TemplateIds = targetProject.TemplateIds.Append(templateId.Value).ToList() }, CancellationToken.None);
            Projects = await _projectStore.LoadProjectsAsync(CancellationToken.None);
            return (true, null, $"Moved \"{Templates.FirstOrDefault(t => t.Id == templateId.Value)?.Name ?? templateId.Value}\" to {targetProject.Name}.");
        }
        catch (Exception ex) { return (false, $"Failed to move template: {ex.Message}", null); }
    }

    // ── Loop Controls ──

    public async Task<(bool Success, string? Error)> StartLoopAsync()
    {
        try
        {
            var started = await _loopService.StartLoopAsync();
            if (!started)
            {
                if (_loopService.ValidationErrors.Count > 0) return (false, "Loop failed to start due to validation errors (see below).");
                if (_loopService.IsLoopActive) return (false, "Loop is already active.");
                return (false, "A manual run is in progress. Wait for it to complete.");
            }
            await _configStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, CancellationToken.None);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start loop: {ex.Message}");
        }
    }

    public async Task StopLoopAsync()
    {
        _loopService.StopLoop();
        await _configStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, CancellationToken.None);
    }

    public void ResumeLoop() => _loopService.ResumeLoop();

    // ── Issue Drawer Data ──

    private async Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template)
    {
        _issueDrawer.Loading = true;
        _issueDrawer.Page = 1;
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) { _issueDrawer.Loading = false; return "Issue provider not found for this template."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = _issueDrawer.SelectedLabels.Count > 0 ? _issueDrawer.SelectedLabels : null;
            var result = await provider.ListOpenIssuesAsync(_issueDrawer.Page, 15, labels, CancellationToken.None);
            _issueDrawer.Items = result.Items.ToList(); _issueDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { _issueDrawer.Items.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { _issueDrawer.Loading = false; }
    }

    private async Task<string?> LoadDrawerLabelsPrivateAsync(PipelineJobTemplate template)
    {
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            _issueDrawer.Labels = labels.ToList();
            return null;
        }
        catch
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
    }

    /// <summary>Public pagination-aware loader for issue drawer (used by Switch path and code-behind).</summary>
    public async Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page)
    {
        _issueDrawer.Loading = true;
        _issueDrawer.Page = page;
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) { _issueDrawer.Loading = false; return "Issue provider not found for this template."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = _issueDrawer.SelectedLabels.Count > 0 ? _issueDrawer.SelectedLabels : null;
            var result = await provider.ListOpenIssuesAsync(page, 15, labels, CancellationToken.None);
            _issueDrawer.Items = result.Items.ToList(); _issueDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { _issueDrawer.Items.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { _issueDrawer.Loading = false; }
    }

    // Backward-compatible wrapper for existing code-behind pagination
    public Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
        => LoadDrawerIssuesPageAsync(template, page);

    public Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
        => LoadDrawerLabelsPrivateAsync(template);

    /// <summary>
    /// Checks dependency readiness for all current drawer issues asynchronously.
    /// Results are stored in <see cref="DrawerReadiness"/> and the caller is notified via onProgress.
    /// </summary>
    public async Task CheckDrawerDependenciesAsync(PipelineJobTemplate template, Action? onProgress = null, CancellationToken cancellationToken = default)
    {
        var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (providerConfig == null) return;

        var issues = _issueDrawer.Items.ToList(); // snapshot
        var stateCache = new Dictionary<int, bool>();

        try
        {
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            foreach (var issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _dependencyChecker.CheckAsync(
                    issue.Identifier, issue.Description, provider, stateCache, cancellationToken);
                DrawerReadiness[issue.Identifier] = result;
                onProgress?.Invoke();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort: partial results are still useful
        }
    }

    public void ClearDrawerIssues() { _issueDrawer.Items.Clear(); _issueDrawer.Page = 1; _issueDrawer.HasMore = false; DrawerReadiness.Clear(); _issueDrawer.SelectedLabels.Clear(); }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (_workDistributor.RequiresConnectedAgents && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var depProviderConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (depProviderConfig != null)
        {
            await using var issueProvider = _providerFactory.CreateIssueProvider(depProviderConfig);
            var depResult = await _dependencyChecker.CheckAsync(issue.Identifier, issue.Description, issueProvider, new Dictionary<int, bool>(), CancellationToken.None);
            if (!depResult.IsReady)
                return (false, $"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", depResult.BlockedBy.Select(n => $"#{n}"))}", null);
        }

        // DB mode: use full orchestration to build a complete request with ProviderConfigs + token vending
        if (_dispatchOrchestration is not null)
        {
            return await DispatchWithOrchestrationAsync(
                template.Id,
                project => _dispatchOrchestration.PrepareDistributionRequestAsync(
                    new ImplementationDispatchOrchestrationRequest
                    {
                        IssueIdentifier = issue.Identifier,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        PipelineProviderId = template.PipelineProviderId,
                        InitiatedBy = InitiatedByManual,
                        Project = project
                    }, CancellationToken.None),
                "Could not dispatch — distribution failed.",
                $"⏳ Queued #{issue.Identifier} — waiting for an idle agent",
                $"✅ Dispatched #{issue.Identifier}");
        }

        // Legacy mode: pass minimal identifiers to LegacyWorkDistributor
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, issue, initiatedBy: InitiatedByManual, timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        return await DispatchLegacyAsync(minimalRequest,
            $"✅ Dispatched #{issue.Identifier}",
            "Could not dispatch — issue is already being processed or queued, or no agents are available.");
    }

    // ── PR Drawer Data ──

    private async Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template)
    {
        _prDrawer.Loading = true;
        _prDrawer.Page = 1;
        try
        {
            var repoConfig = RepoProviders.FirstOrDefault(p => p.Id == template.RepoProviderId);
            if (repoConfig == null) { _prDrawer.Items = new(); _prDrawer.Loading = false; return null; }
            await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
            var labels = _prDrawer.SelectedLabels.Count > 0 ? _prDrawer.SelectedLabels : null;
            var result = await repoProvider.ListOpenPullRequestsAsync(_prDrawer.Page, 15, labels, CancellationToken.None);
            _prDrawer.Items = result.Items.ToList(); _prDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { _prDrawer.Items = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { _prDrawer.Loading = false; }
    }

    private async Task<string?> LoadPrDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            _prDrawer.Labels = labels.ToList();
            return null;
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to load PR drawer labels"); _prDrawer.Labels.Clear(); return null; }
    }

    /// <summary>Public pagination-aware loader for PR drawer.</summary>
    public async Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page)
    {
        _prDrawer.Loading = true;
        _prDrawer.Page = page;
        try
        {
            var repoConfig = RepoProviders.FirstOrDefault(p => p.Id == template.RepoProviderId);
            if (repoConfig == null) { _prDrawer.Items = new(); _prDrawer.Loading = false; return null; }
            await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
            var labels = _prDrawer.SelectedLabels.Count > 0 ? _prDrawer.SelectedLabels : null;
            var result = await repoProvider.ListOpenPullRequestsAsync(page, 15, labels, CancellationToken.None);
            _prDrawer.Items = result.Items.ToList(); _prDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { _prDrawer.Items = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { _prDrawer.Loading = false; }
    }

    public void ClearPrDrawerLabelFilter() { _prDrawer.Items.Clear(); _prDrawer.Page = 1; _prDrawer.HasMore = false; _prDrawer.SelectedLabels.Clear(); }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(
        PullRequestSummary pr, PipelineJobTemplate template)
    {
        if (_workDistributor.RequiresConnectedAgents && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        // DB mode: use full orchestration for ProviderConfigs + RunId + token vending
        if (_dispatchOrchestration is not null)
        {
            return await DispatchWithOrchestrationAsync(
                template.Id,
                project =>
                {
                    var reviewRequest = new ReviewDispatchRequest
                    {
                        PrIdentifier = pr.Identifier,
                        PrBranchName = pr.BranchName,
                        PrTitle = pr.Title ?? "",
                        PrUrl = pr.Url,
                        PrTargetBranch = pr.TargetBranch,
                        PrDescription = pr.Description,
                        PrAuthor = pr.Author,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = InitiatedByManual
                    };
                    return _dispatchOrchestration.PrepareReviewDistributionRequestAsync(
                        reviewRequest, project, CancellationToken.None);
                },
                $"PR #{pr.Identifier} is already being processed or queued.",
                $"⏳ Queued PR #{pr.Identifier} for review — waiting for an idle agent",
                $"PR #{pr.Identifier} dispatched for review.");
        }

        // Legacy mode
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: InitiatedByManual, timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        return await DispatchLegacyAsync(minimalRequest,
            $"PR #{pr.Identifier} dispatched for review.",
            $"PR #{pr.Identifier} is already being processed or queued.");
    }

    // ── Epic Drawer Data ──

    private async Task<string?> LoadEpicDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : template.IssueProviderId;
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            _epicDrawer.Labels = labels.Where(l => !l.StartsWith(AgentLabels.Epic, StringComparison.OrdinalIgnoreCase)).ToList();
            return null;
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to load epic drawer labels"); _epicDrawer.Labels.Clear(); return null; }
    }

    /// <summary>Public pagination-aware loader for epic drawer.</summary>
    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
    {
        _epicDrawer.Loading = true;
        _epicDrawer.Page = page;
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : template.IssueProviderId;
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == epicProviderId);
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

    public void ClearEpicDrawerIssues() { _epicDrawer.Items.Clear(); _epicDrawer.Page = 1; _epicDrawer.HasMore = false; _epicDrawer.SelectedLabels.Clear(); }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (_workDistributor.RequiresConnectedAgents && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var phaseType = issue.Labels.Contains(AgentLabels.EpicApproved, StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
        var phaseLabel = phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

        // DB mode: use full orchestration
        if (_dispatchOrchestration is not null)
        {
            return await DispatchWithOrchestrationAsync(
                template.Id,
                project => _dispatchOrchestration.PrepareDecompositionDistributionRequestAsync(
                    new DecompositionDispatchOrchestrationRequest
                    {
                        EpicIdentifier = issue.Identifier,
                        EpicTitle = issue.Title ?? "",
                        PhaseType = phaseType,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = InitiatedByManual,
                        Project = project
                    }, CancellationToken.None),
                "Could not dispatch — epic is already being processed or queued, or no agents are available.",
                $"⏳ Queued epic #{issue.Identifier} for {phaseLabel} — waiting for an idle agent",
                $"✅ Dispatched epic #{issue.Identifier} for {phaseLabel}");
        }

        // Legacy mode
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, issue, phaseType, initiatedBy: InitiatedByManual, timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        return await DispatchLegacyAsync(minimalRequest,
            $"✅ Dispatched epic #{issue.Identifier} for {phaseLabel}",
            "Could not dispatch — epic is already being processed or queued, or no agents are available.");
    }

    // ── Drawer Orchestration ──

    /// <summary>Refreshes the cached set of active issue identifiers from <see cref="IWorkDistributor"/>.</summary>
    public async Task RefreshActiveIssuesAsync()
    {
        ActiveIssues = await _workDistributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);
    }

    /// <summary>Synchronous check against the preloaded active issues set.</summary>
    public bool IsIssueActive(IssueIdentifier issueIdentifier, string issueProviderConfigId)
        => ActiveIssues.Contains((issueIdentifier, issueProviderConfigId));

    private void HideOtherDrawers(string keepOpen)
    {
        if (keepOpen != DrawerTabIssue) _issueDrawer.IsOpen = false;
        if (keepOpen != DrawerTabPr) _prDrawer.IsOpen = false;
        if (keepOpen != DrawerTabEpic) _epicDrawer.IsOpen = false;
    }

    /// <summary>Closes whichever drawer is currently open.</summary>
    public void CloseActiveDrawer()
    {
        if (_issueDrawer.IsOpen) _issueDrawer.Close();
        else if (_prDrawer.IsOpen) _prDrawer.Close();
        else if (_epicDrawer.IsOpen) _epicDrawer.Close();
    }

    // ── Issue Drawer Orchestration ──

    public async Task<string?> OpenIssueDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        var template = Templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        HideOtherDrawers(DrawerTabIssue);
        await RefreshActiveIssuesAsync();
        return await _issueDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseIssueDrawer() { _issueDrawer.Close(); DrawerReadiness.Clear(); }

    public Task<string?> SwitchToIssueDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        HideOtherDrawers(DrawerTabIssue);
        return _issueDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _issueDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = Templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                await RefreshActiveIssuesAsync();
                return template;
            });
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue)
        => _issueDrawer.DispatchAsync(issue, null);

    // ── PR Drawer Orchestration ──

    // TODO: Contract change — the prior string overload had an explicit `if (string.IsNullOrEmpty(templateId)) return null;`
    // guard that was removed when adopting TemplateId. The implicit conversion operator on TemplateId calls
    // ArgumentException.ThrowIfNullOrEmpty, so callers passing an empty string will now throw instead of getting
    // a graceful null return. UI call sites are guarded by disabled buttons, but any future non-UI caller
    // passing an optional/empty template ID must use `TemplateId?` and null-check before calling this method.
    public async Task<string?> OpenPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        var template = Templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        HideOtherDrawers(DrawerTabPr);
        return await _prDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void ClosePrDrawer() => _prDrawer.Close();

    public Task<string?> SwitchToPrDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        HideOtherDrawers(DrawerTabPr);
        return _prDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _prDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = Templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(PullRequestSummary pr)
        => _prDrawer.DispatchAsync(pr, null);

    // ── Epic Drawer Orchestration ──

    // TODO: Same contract change as OpenPrDrawerAsync — the prior string overload had an explicit
    // `if (string.IsNullOrEmpty(templateId)) return null;` guard that was removed when adopting TemplateId.
    // Passing an empty string will throw via ArgumentException.ThrowIfNullOrEmpty. Non-UI callers must
    // use `TemplateId?` and null-check before calling this method.
    public async Task<string?> OpenEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        var template = Templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        HideOtherDrawers(DrawerTabEpic);
        return await _epicDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawer.Close();

    public Task<string?> SwitchToEpicDrawerAsync(TemplateId templateId, Func<Task>? notifyStateChanged = null)
    {
        HideOtherDrawers(DrawerTabEpic);
        return _epicDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _epicDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = Templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(IssueSummary issue)
        => _epicDrawer.DispatchAsync(issue, null);

    // ── Dependency Check (with CTS) ──

    private async Task CheckDrawerDependenciesInBackgroundAsync(PipelineJobTemplate template)
    {
        var token = _issueDrawer.CancellationToken;
        try
        {
            await CheckDrawerDependenciesAsync(template, null, token);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

    // ── Helpers ──

    /// <summary>
    /// Shared orchestration dispatch flow: resolve project → prepare request → distribute →
    /// revert on failure / confirm label on direct dispatch → return result tuple.
    /// </summary>
    private async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchWithOrchestrationAsync(
        TemplateId templateId,
        Func<PipelineProject, Task<JobDistributionRequest?>> prepareAsync,
        string distributionFailedError,
        string queuedMessage,
        string dispatchedMessage)
    {
        var project = GetParentProject(templateId) ?? new PipelineProject { Id = "", Name = "Unknown" };
        var request = await prepareAsync(project);

        if (request is null)
            return (false, "Could not dispatch — orchestration preparation failed (check logs for details).", null);

        var outcome = await _dispatchOrchestration!.DistributeAndFinalizeAsync(request, CancellationToken.None);
        if (!outcome.Success)
            return (false, distributionFailedError, null);

        return (true, null, outcome.Queued ? queuedMessage : dispatchedMessage);
    }

    /// <summary>
    /// Shared legacy dispatch flow: distribute a pre-built request and return success/failure messages.
    /// </summary>
    private async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchLegacyAsync(
        JobDistributionRequest request,
        string successMessage,
        string failureError)
    {
        var result = await _workDistributor.DistributeAsync(request, CancellationToken.None);
        return result.Success
            ? (true, null, successMessage)
            : (false, failureError, null);
    }

    /// <summary>
    /// Checks if an issue is currently distributed (Pending, Dispatched, or Running)
    /// via <see cref="IWorkDistributor.IsIssueDistributedAsync"/>.
    /// Used by drawer components to show processing status.
    /// </summary>
    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId)
        => _workDistributor.IsIssueDistributedAsync(issueIdentifier, issueProviderConfigId, CancellationToken.None);

    public PipelineProject? GetParentProject(TemplateId templateId) =>
        Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId.Value));

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _issueDrawer.Dispose();
            _prDrawer.Dispose();
            _epicDrawer.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}