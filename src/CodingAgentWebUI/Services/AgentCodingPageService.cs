using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Dispatch;
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
    private readonly PipelineLoopService _loopService;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly IConfigurationStore _configStore;
    private readonly IProjectStore _projectStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IDependencyChecker _dependencyChecker;

    public AgentCodingPageService(
        PipelineLoopService loopService,
        IJobDispatcher jobDispatcher,
        IConfigurationStore configStore,
        IProjectStore projectStore,
        IProviderFactory providerFactory,
        IDependencyChecker dependencyChecker)
    {
        _loopService = loopService;
        _jobDispatcher = jobDispatcher;
        _configStore = configStore;
        _projectStore = projectStore;
        _providerFactory = providerFactory;
        _dependencyChecker = dependencyChecker;
    }

    // ── State ──

    public List<PipelineJobTemplate> Templates { get; private set; } = new();
    public IReadOnlyList<PipelineProject> Projects { get; private set; } = [];
    public List<ProviderConfig> IssueProviders { get; private set; } = new();
    public List<ProviderConfig> RepoProviders { get; private set; } = new();
    public List<ProviderConfig> PipelineProviders { get; private set; } = new();
    public List<ProviderConfig> BrainProviders { get; private set; } = new();
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

    public List<PullRequestSummary> PrDrawerPrs { get; private set; } = new();
    public int PrDrawerPage { get; private set; } = 1;
    public bool PrDrawerHasMore { get; private set; }
    public bool PrDrawerLoading { get; private set; }

    public List<IssueSummary> EpicDrawerIssues { get; private set; } = new();
    public bool EpicDrawerLoading { get; private set; }

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
            var result = await provider.ListOpenIssuesAsync(page, 25, CancellationToken.None);
            DrawerIssues = result.Items.ToList(); DrawerHasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { DrawerIssues.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { DrawerLoading = false; }
    }

    public void ClearDrawerIssues() { DrawerIssues.Clear(); DrawerPage = 1; DrawerHasMore = false; }

    /// <summary>
    /// Batch-checks dependency readiness for a list of issues. Returns results progressively
    /// via the onProgress callback. Provider lifecycle is managed internally.
    /// </summary>
    public async Task CheckDrawerReadinessAsync(
        IReadOnlyList<IssueSummary> issues,
        string issueProviderId,
        Dictionary<int, bool> stateCache,
        Action<string, DependencyCheckResult> onResult,
        CancellationToken ct)
    {
        var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == issueProviderId);
        if (providerConfig == null) return;

        await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _dependencyChecker.CheckAsync(
                    issue.Identifier, issue.Description, provider, stateCache, ct);
                onResult(issue.Identifier, result);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* treat individual failures as unknown — skip */ }
        }
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (!_jobDispatcher.HasRegisteredAgents)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var depProviderConfig = IssueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (depProviderConfig != null)
        {
            await using var issueProvider = _providerFactory.CreateIssueProvider(depProviderConfig);
            var depResult = await _dependencyChecker.CheckAsync(issue.Identifier, issue.Description, issueProvider, new Dictionary<int, bool>(), CancellationToken.None);
            if (!depResult.IsReady)
                return (false, $"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", depResult.BlockedBy.Select(n => $"#{n}"))}", null);
        }

        var dispatched = await _jobDispatcher.TryDispatchAsync(issue.Identifier, template.IssueProviderId, template.RepoProviderId,
            template.BrainProviderId, template.PipelineProviderId, initiatedBy: "manual", CancellationToken.None,
            issueTitle: issue.Title, project: GetParentProject(template.Id));
        if (dispatched) return (true, null, $"✅ Dispatched #{issue.Identifier}");
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
            var result = await repoProvider.ListOpenPullRequestsAsync(page, 30, null, CancellationToken.None);
            PrDrawerPrs = result.Items.ToList(); PrDrawerHasMore = result.HasMore;
            return null;
        }
        catch (Exception ex) { PrDrawerPrs = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { PrDrawerLoading = false; }
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(
        PullRequestSummary pr, PipelineJobTemplate template)
    {
        if (!_jobDispatcher.HasRegisteredAgents)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var dispatched = await _jobDispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
        {
            PrIdentifier = pr.Identifier, PrBranchName = pr.BranchName, PrTitle = pr.Title,
            PrDescription = pr.Description, PrAuthor = pr.Author, PrUrl = pr.Url, PrTargetBranch = pr.TargetBranch,
            IssueProviderId = template.IssueProviderId, RepoProviderId = template.RepoProviderId,
            BrainProviderId = template.BrainProviderId, InitiatedBy = "manual"
        }, CancellationToken.None, project: GetParentProject(template.Id));
        if (dispatched) return (true, null, $"PR #{pr.Identifier} dispatched for review.");
        return (false, $"PR #{pr.Identifier} is already being processed or queued.", null);
    }

    // ── Epic Drawer ──

    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template)
    {
        EpicDrawerLoading = true;
        try
        {
            var parentProject = GetParentProject(template.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : template.IssueProviderId;
            var providerConfig = IssueProviders.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) { EpicDrawerLoading = false; return "Epic issue provider not found."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var epicResult = await provider.ListOpenIssuesAsync(1, 50, new[] { "agent:epic" }, CancellationToken.None);
            var approvedResult = await provider.ListOpenIssuesAsync(1, 50, new[] { "agent:epic-approved" }, CancellationToken.None);
            EpicDrawerIssues = epicResult.Items.Concat(approvedResult.Items).ToList();
            return null;
        }
        catch (Exception ex) { EpicDrawerIssues.Clear(); return $"Failed to load epics: {ex.Message}"; }
        finally { EpicDrawerLoading = false; }
    }

    public void ClearEpicDrawerIssues() => EpicDrawerIssues.Clear();

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue, PipelineJobTemplate template)
    {
        if (!IssueProviders.Any(p => p.Id == template.IssueProviderId) || !RepoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);
        if (!_jobDispatcher.HasRegisteredAgents)
            return (false, "Could not dispatch — no agents are currently connected.", null);

        var phaseType = issue.Labels.Contains("agent:epic-approved", StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
        var dispatched = await _jobDispatcher.TryDispatchDecompositionAsync(issue.Identifier, issue.Title, phaseType,
            template.IssueProviderId, template.RepoProviderId, template.BrainProviderId,
            initiatedBy: "manual", CancellationToken.None, project: GetParentProject(template.Id));
        if (dispatched) return (true, null, $"✅ Dispatched epic #{issue.Identifier} for {(phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition")}");
        return (false, "Could not dispatch — epic is already being processed or queued, or no agents are available.", null);
    }

    // ── Helpers ──

    public PipelineProject? GetParentProject(string templateId) =>
        Projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId));
}
