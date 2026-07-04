using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Encapsulates the business logic for the AgentCoding page — template CRUD, drawer operations,
/// loop controls, and dispatch coordination. The Blazor component delegates to this service
/// and retains only UI state (visibility, timers, JS interop, StateHasChanged).
/// Registered as Scoped because it holds per-page mutable state.
/// </summary>
public class AgentCodingPageService
{
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

    // Drawer state
    public List<IssueSummary> DrawerIssues { get; private set; } = new();
    public int DrawerPage { get; private set; } = 1;
    public bool DrawerHasMore { get; private set; }
    public bool DrawerLoading { get; private set; }
    public Dictionary<string, DependencyCheckResult> DrawerReadiness { get; private set; } = new();
    public List<string> DrawerLabels { get; private set; } = new();
    public List<string> DrawerSelectedLabels { get; private set; } = new();

    public List<PullRequestSummary> PrDrawerPrs { get; private set; } = new();
    public int PrDrawerPage { get; private set; } = 1;
    public bool PrDrawerHasMore { get; private set; }
    public bool PrDrawerLoading { get; private set; }
    public List<string> PrDrawerLabels { get; private set; } = new();
    public List<string> PrDrawerSelectedLabels { get; private set; } = new();

    public List<IssueSummary> EpicDrawerIssues { get; private set; } = new();
    public int EpicDrawerPage { get; private set; } = 1;
    public bool EpicDrawerHasMore { get; private set; }
    public bool EpicDrawerLoading { get; private set; }
    public List<string> EpicDrawerLabels { get; private set; } = new();
    public List<string> EpicDrawerSelectedLabels { get; private set; } = new();

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

    public async Task<(bool Success, string? Error)> ToggleTemplateEnabledAsync(PipelineJobTemplate template, bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = template with { Enabled = enabled };
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _projectStore.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}"); }
        Templates[idx] = updated;
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleImplementationEnabledAsync(PipelineJobTemplate template, bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = template with { ImplementationEnabled = enabled };
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _projectStore.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}"); }
        Templates[idx] = updated;
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleReviewEnabledAsync(PipelineJobTemplate template, bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = template with { ReviewEnabled = enabled };
        var projectId = GetParentProject(template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await _projectStore.SaveTemplateAsync(projectId, updated, CancellationToken.None); }
        catch (Exception ex) { return (false, $"Failed to save: {ex.Message}"); }
        Templates[idx] = updated;
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleDecompositionEnabledAsync(PipelineJobTemplate template, bool enabled)
    {
        var idx = Templates.FindIndex(t => t.Id == template.Id);
        if (idx < 0) return (true, null);
        var updated = template with { DecompositionEnabled = enabled };
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
            ReviewEnabled = form.ReviewEnabled, DecompositionEnabled = form.DecompositionEnabled, Enabled = true
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
        string templateId, string sourceProjectId, string targetProjectId)
    {
        try
        {
            var sourceProject = Projects.FirstOrDefault(p => p.Id == sourceProjectId);
            var targetProject = Projects.FirstOrDefault(p => p.Id == targetProjectId);
            if (sourceProject == null || targetProject == null) return (true, null, null);
            await _projectStore.SaveProjectAsync(sourceProject with { TemplateIds = sourceProject.TemplateIds.Where(id => id != templateId).ToList() }, CancellationToken.None);
            await _projectStore.SaveProjectAsync(targetProject with { TemplateIds = targetProject.TemplateIds.Append(templateId).ToList() }, CancellationToken.None);
            Projects = await _projectStore.LoadProjectsAsync(CancellationToken.None);
            return (true, null, $"Moved \"{Templates.FirstOrDefault(t => t.Id == templateId)?.Name ?? templateId}\" to {targetProject.Name}.");
        }
        catch (Exception ex) { return (false, $"Failed to move template: {ex.Message}", null); }
    }

    // ── Loop Controls ──

    public async Task<(bool Success, string? Error)> StartLoopAsync()
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

    public async Task StopLoopAsync()
    {
        _loopService.StopLoop();
        await _configStore.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, CancellationToken.None);
    }

    public void ResumeLoop() => _loopService.ResumeLoop();

    // ── Issue Drawer ──

    public async Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
    {
        DrawerLoading = true;
        DrawerPage = page;
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) { DrawerLoading = false; return "Issue provider not found for this template."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = DrawerSelectedLabels.Count > 0 ? DrawerSelectedLabels : null;
            var result = await provider.ListOpenIssuesAsync(page, 15, labels, CancellationToken.None);
            DrawerIssues = result.Items.ToList(); DrawerHasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { DrawerIssues.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { DrawerLoading = false; }
    }

    public async Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null; // non-fatal, just no label filter
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            DrawerLabels = labels.ToList();
            return null;
        }
        catch
        {
            DrawerLabels.Clear();
            return null; // non-fatal
        }
    }

    public void ToggleDrawerLabel(string label)
    {
        if (DrawerSelectedLabels.Contains(label))
            DrawerSelectedLabels.Remove(label);
        else
            DrawerSelectedLabels.Add(label);
    }

    public void ClearDrawerLabelFilter() => DrawerSelectedLabels.Clear();

    public void ClearDrawerIssues() { DrawerIssues.Clear(); DrawerPage = 1; DrawerHasMore = false; DrawerReadiness.Clear(); DrawerSelectedLabels.Clear(); }

    /// <summary>
    /// Checks dependency readiness for all current drawer issues asynchronously.
    /// Results are stored in <see cref="DrawerReadiness"/> and the caller is notified via onProgress.
    /// </summary>
    // TODO: Accept a CancellationToken and cancel on drawer close/page navigation to prevent concurrent mutations on DrawerReadiness from overlapping invocations.
    public async Task CheckDrawerDependenciesAsync(PipelineJobTemplate template, Action? onProgress = null)
    {
        var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (providerConfig == null) return;

        var issues = DrawerIssues.ToList(); // snapshot
        var stateCache = new Dictionary<int, bool>();

        try
        {
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            foreach (var issue in issues)
            {
                var result = await _dependencyChecker.CheckAsync(
                    issue.Identifier, issue.Description, provider, stateCache, CancellationToken.None);
                DrawerReadiness[issue.Identifier] = result;
                onProgress?.Invoke();
            }
        }
        catch
        {
            // TODO: Log the exception at Warning level for diagnosability; re-throw OperationCanceledException when cancellation support is added.
            // Best-effort: partial results are still useful
        }
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (_workDistributor is LegacyWorkDistributor && _agentRegistry.GetAllAgents().Count == 0)
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
            var project = GetParentProject(template.Id) ?? new PipelineProject { Id = "", Name = "Unknown" };
            var request = await _dispatchOrchestration.PrepareDistributionRequestAsync(
                issue.Identifier,
                template.IssueProviderId, template.RepoProviderId,
                template.BrainProviderId, template.PipelineProviderId,
                "manual", project,
                ct: CancellationToken.None);

            if (request is null)
                return (false, "Could not dispatch — orchestration preparation failed (check logs for details).", null);

            var result = await _workDistributor.DistributeAsync(request, CancellationToken.None);
            if (!result.Success)
            {
                await _dispatchOrchestration.RevertFailedDistributionAsync(request, CancellationToken.None);
                return (false, "Could not dispatch — distribution failed.", null);
            }

            if (!result.Queued)
                // TODO: Consider propagating the request's cancellation token instead of CancellationToken.None
                await _dispatchOrchestration.ConfirmDistributionLabelAsync(request, CancellationToken.None);

            return (true, null, result.Queued
                ? $"⏳ Queued #{issue.Identifier} — waiting for an idle agent"
                : $"✅ Dispatched #{issue.Identifier}");
        }

        // Legacy mode: pass minimal identifiers to LegacyWorkDistributor
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, issue, initiatedBy: "manual", timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        var legacyResult = await _workDistributor.DistributeAsync(minimalRequest, CancellationToken.None);
        if (legacyResult.Success) return (true, null, $"✅ Dispatched #{issue.Identifier}");
        return (false, "Could not dispatch — issue is already being processed or queued, or no agents are available.", null);
    }

    // ── PR Drawer ──

    public async Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page)
    {
        PrDrawerLoading = true;
        PrDrawerPage = page;
        try
        {
            var repoConfig = RepoProviders.FirstOrDefault(p => p.Id == template.RepoProviderId);
            if (repoConfig == null) { PrDrawerPrs = new(); PrDrawerLoading = false; return null; }
            await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
            var labels = PrDrawerSelectedLabels.Count > 0 ? PrDrawerSelectedLabels : null;
            var result = await repoProvider.ListOpenPullRequestsAsync(page, 15, labels, CancellationToken.None);
            PrDrawerPrs = result.Items.ToList(); PrDrawerHasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { PrDrawerPrs = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { PrDrawerLoading = false; }
    }

    public async Task<string?> LoadPrDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            PrDrawerLabels = labels.ToList();
            return null;
        }
        catch { PrDrawerLabels.Clear(); return null; }
    }

    public void TogglePrDrawerLabel(string label)
    {
        if (PrDrawerSelectedLabels.Contains(label)) PrDrawerSelectedLabels.Remove(label);
        else PrDrawerSelectedLabels.Add(label);
    }

    public void ClearPrDrawerLabelFilter() { PrDrawerPrs.Clear(); PrDrawerPage = 1; PrDrawerHasMore = false; PrDrawerSelectedLabels.Clear(); }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(
        PullRequestSummary pr, PipelineJobTemplate template)
    {
        if (_workDistributor is LegacyWorkDistributor && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        // DB mode: use full orchestration for ProviderConfigs + RunId + token vending
        if (_dispatchOrchestration is not null)
        {
            var project = GetParentProject(template.Id) ?? new PipelineProject { Id = "", Name = "Unknown" };
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
                InitiatedBy = "manual"
            };
            var request = await _dispatchOrchestration.PrepareReviewDistributionRequestAsync(
                reviewRequest, project, CancellationToken.None);

            if (request is null)
                return (false, "Could not dispatch — orchestration preparation failed (check logs for details).", null);

            var result = await _workDistributor.DistributeAsync(request, CancellationToken.None);
            if (!result.Success)
            {
                await _dispatchOrchestration.RevertFailedDistributionAsync(request, CancellationToken.None);
                return (false, $"PR #{pr.Identifier} is already being processed or queued.", null);
            }

            if (!result.Queued)
                await _dispatchOrchestration.ConfirmDistributionLabelAsync(request, CancellationToken.None);

            return (true, null, result.Queued
                ? $"⏳ Queued PR #{pr.Identifier} for review — waiting for an idle agent"
                : $"PR #{pr.Identifier} dispatched for review.");
        }

        // Legacy mode
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, pr, initiatedBy: "manual", timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        var legacyResult = await _workDistributor.DistributeAsync(minimalRequest, CancellationToken.None);
        if (legacyResult.Success) return (true, null, $"PR #{pr.Identifier} dispatched for review.");
        return (false, $"PR #{pr.Identifier} is already being processed or queued.", null);
    }

    // ── Epic Drawer ──

    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
    {
        EpicDrawerLoading = true;
        EpicDrawerPage = page;
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : template.IssueProviderId;
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) { EpicDrawerLoading = false; return "Epic issue provider not found."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);

            // Build label filter: always include epic markers + any user-selected labels
            var epicLabels = new List<string> { "agent:epic" };
            if (EpicDrawerSelectedLabels.Count > 0)
                epicLabels.AddRange(EpicDrawerSelectedLabels);

            var approvedLabels = new List<string> { "agent:epic-approved" };
            if (EpicDrawerSelectedLabels.Count > 0)
                approvedLabels.AddRange(EpicDrawerSelectedLabels);

            var epicResult = await provider.ListOpenIssuesAsync(page, 8, epicLabels, CancellationToken.None);
            var approvedResult = await provider.ListOpenIssuesAsync(page, 8, approvedLabels, CancellationToken.None);

            // Deduplicate: issues with both agent:epic and agent:epic-approved appear in both queries
            EpicDrawerIssues = epicResult.Items.Concat(approvedResult.Items)
                .GroupBy(i => i.Identifier)
                .Select(g => g.First())
                .ToList();
            EpicDrawerHasMore = epicResult.HasMore || approvedResult.HasMore;
            return null;
        }
        catch (Exception ex) { EpicDrawerIssues.Clear(); return $"Failed to load epics: {ex.Message}"; }
        finally { EpicDrawerLoading = false; }
    }

    public async Task<string?> LoadEpicDrawerLabelsAsync(PipelineJobTemplate template)
    {
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : template.IssueProviderId;
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(CancellationToken.None);
            // Exclude the epic markers themselves from the filter UI
            EpicDrawerLabels = labels.Where(l => !l.StartsWith("agent:epic", StringComparison.OrdinalIgnoreCase)).ToList();
            return null;
        }
        catch { EpicDrawerLabels.Clear(); return null; }
    }

    public void ToggleEpicDrawerLabel(string label)
    {
        if (EpicDrawerSelectedLabels.Contains(label)) EpicDrawerSelectedLabels.Remove(label);
        else EpicDrawerSelectedLabels.Add(label);
    }

    public void ClearEpicDrawerLabelFilter() => EpicDrawerSelectedLabels.Clear();

    public void ClearEpicDrawerIssues() { EpicDrawerIssues.Clear(); EpicDrawerPage = 1; EpicDrawerHasMore = false; EpicDrawerSelectedLabels.Clear(); }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (_workDistributor is LegacyWorkDistributor && _agentRegistry.GetAllAgents().Count == 0)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var phaseType = issue.Labels.Contains("agent:epic-approved", StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;

        // DB mode: use full orchestration
        if (_dispatchOrchestration is not null)
        {
            var project = GetParentProject(template.Id) ?? new PipelineProject { Id = "", Name = "Unknown" };
            var request = await _dispatchOrchestration.PrepareDecompositionDistributionRequestAsync(
                issue.Identifier, issue.Title ?? "", phaseType,
                template.IssueProviderId, template.RepoProviderId, template.BrainProviderId,
                "manual", project, ct: CancellationToken.None);

            if (request is null)
                return (false, "Could not dispatch — orchestration preparation failed (check logs for details).", null);

            var result = await _workDistributor.DistributeAsync(request, CancellationToken.None);
            if (!result.Success)
            {
                await _dispatchOrchestration.RevertFailedDistributionAsync(request, CancellationToken.None);
                return (false, "Could not dispatch — epic is already being processed or queued, or no agents are available.", null);
            }

            if (!result.Queued)
                await _dispatchOrchestration.ConfirmDistributionLabelAsync(request, CancellationToken.None);

            var phaseLabel = phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";
            return (true, null, result.Queued
                ? $"⏳ Queued epic #{issue.Identifier} for {phaseLabel} — waiting for an idle agent"
                : $"✅ Dispatched epic #{issue.Identifier} for {phaseLabel}");
        }

        // Legacy mode
        var minimalRequest = JobDistributionRequest.FromTemplate(
            template, issue, phaseType, initiatedBy: "manual", timeoutSeconds: 3600,
            projectId: GetParentProject(template.Id)?.Id, projectName: GetParentProject(template.Id)?.Name);
        var legacyResult = await _workDistributor.DistributeAsync(minimalRequest, CancellationToken.None);
        if (legacyResult.Success) return (true, null, $"✅ Dispatched epic #{issue.Identifier} for {(phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition")}");
        return (false, "Could not dispatch — epic is already being processed or queued, or no agents are available.", null);
    }

    // ── Helpers ──

    /// <summary>
    /// Checks if an issue is currently distributed (Pending, Dispatched, or Running)
    /// via <see cref="IWorkDistributor.IsIssueDistributedAsync"/>.
    /// Used by drawer components to show processing status.
    /// </summary>
    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId)
        => _workDistributor.IsIssueDistributedAsync(issueIdentifier, issueProviderConfigId, CancellationToken.None);

    public PipelineProject? GetParentProject(string templateId) =>
        Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId));
}
