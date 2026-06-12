using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Serilog;

namespace CodingAgentWebUI.Components.Pages;

// TODO: Consider further decomposition — this code-behind is 538 lines. The business logic
// (drawer operations, template CRUD, loop controls) could be extracted into a dedicated service
// or ViewModel to reduce the partial class size.
public partial class AgentCoding : IDisposable
{
    private const string JsScrollToBottom = "scrollToBottom";
    private const string JsScrollActiveStep = "scrollActiveStepIntoView";

    [Inject] private PipelineOrchestrationService PipelineService { get; set; } = default!;
    [Inject] private PipelineLoopService LoopService { get; set; } = default!;
    [Inject] private OrchestratorRunService RunService { get; set; } = default!;
    [Inject] private AgentRegistryService Registry { get; set; } = default!;
    [Inject] private JobDispatcherService Dispatcher { get; set; } = default!;
    [Inject] private IJobDispatcher JobDispatcher { get; set; } = default!;
    [Inject] private IConfigurationStore ConfigStore { get; set; } = default!;
    [Inject] private IProjectStore ProjectStore { get; set; } = default!;
    [Inject] private IProviderFactory ProviderFactory { get; set; } = default!;
    [Inject] private IDependencyChecker DependencyChecker { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private List<ProviderConfig> _issueProviders = new();
    private List<ProviderConfig> _repoProviders = new();
    private List<ProviderConfig> _pipelineProviders = new();
    private List<ProviderConfig> _brainProviders = new();
    private readonly object _outputLock = new();
    private List<string> _outputLines = new();

    private IReadOnlyList<QualityGateConfiguration> _qualityGateConfigs = [];
    private IReadOnlyList<ReviewerConfiguration> _reviewerConfigs = [];
    private IReadOnlyList<AgentProfile> _agentProfiles = [];
    private PipelineConfiguration _pipelineConfig = new();

    private string? _errorMessage;
    private string? _successMessage;
    private bool _showAgentSummary = true;
    private bool _hideLoopToast;
    private string? _lastLoopStatus;
    private bool _showCompletionOnly;
    private string? _lastRunId;
    private bool _disposed;
    private Timer? _elapsedTimer;
    private int _maxRetries = 3;

    // Template Table State
    private List<PipelineJobTemplate> _templates = new();
    private IReadOnlyList<PipelineProject> _projects = [];
    private bool _showAddForm;
    private TemplateTableSection.TemplateFormModel _addForm = new();
#pragma warning disable CS0414 // Value is read in .razor partial
    private string? _formError;
#pragma warning restore CS0414
    private bool _showDeleteConfirm;
    private PipelineJobTemplate? _deletingTemplate;
    private HashSet<string> _recentlyToggled = new();

    // Manual Dispatch State
    private string _manualDispatchTemplateId = "";
    private bool _drawerOpen;
    private PipelineJobTemplate? _drawerTemplate;
    private List<IssueSummary> _drawerIssues = new();
    private bool _drawerLoading;
    private int _drawerPage = 1;
    private bool _drawerHasMore;
    private bool _drawerDispatching;

    // PR Drawer State
    private bool _prDrawerOpen;
    private PipelineJobTemplate? _prDrawerTemplate;
    private bool _prDrawerLoading;
    private List<PullRequestSummary> _prDrawerPrs = new();
    private int _prDrawerPage = 1;
    private bool _prDrawerHasMore;
    private bool _prDrawerDispatching;

    // Epic Drawer State
    private bool _epicDrawerOpen;
    private PipelineJobTemplate? _epicDrawerTemplate;
    private bool _epicDrawerLoading;
    private List<IssueSummary> _epicDrawerIssues = new();
    private bool _epicDrawerDispatching;

    private void OnTemplateChanged(ChangeEventArgs e) =>
        _manualDispatchTemplateId = e.Value?.ToString() ?? "";

    protected override async Task OnInitializedAsync()
    {
        PipelineService.OnChange += HandleStateChanged;
        PipelineService.OnOutputLine += HandleOutputLine;
        LoopService.OnChange += HandleStateChanged;
        _elapsedTimer = new Timer(ElapsedTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        try
        {
            _issueProviders = (await ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None)).ToList();
            var allRepoProviders = (await ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None)).ToList();
            _pipelineProviders = (await ConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, CancellationToken.None)).ToList();
            _brainProviders = allRepoProviders.Where(p => p.RepositoryRole == RepositoryRole.Brain).ToList();
            _repoProviders = allRepoProviders.Where(p => p.RepositoryRole != RepositoryRole.Brain).ToList();

            var config = await ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            _maxRetries = config.MaxRetries;
            _templates = (await ProjectStore.LoadAllTemplatesAsync(CancellationToken.None)).ToList();
            _pipelineConfig = config;
            _projects = await ProjectStore.LoadProjectsAsync(CancellationToken.None);
            _qualityGateConfigs = await ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            _reviewerConfigs = await ConfigStore.LoadReviewerConfigsAsync(CancellationToken.None);
            _agentProfiles = await ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        }
        catch (Exception ex) { _errorMessage = $"Failed to load configuration: {ex.Message}"; }

        _ = AutoDismissAgentSummary();
    }

    // ── Template Table Callbacks ──

    private void ShowAddForm()
    {
        _addForm = new TemplateTableSection.TemplateFormModel();
        var defaultProject = _projects.FirstOrDefault(p => p.Id == WellKnownIds.DefaultProjectId) ?? _projects.FirstOrDefault();
        if (defaultProject != null) _addForm.ProjectId = defaultProject.Id;
        _formError = null;
        _showAddForm = true;
    }

    private void CancelAddForm() { _showAddForm = false; _formError = null; }
    private void CancelDelete() => _showDeleteConfirm = false;
    private void ConfirmRemoveTemplate(PipelineJobTemplate template) { _deletingTemplate = template; _showDeleteConfirm = true; }

    private async Task ToggleTemplateEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var idx = _templates.FindIndex(t => t.Id == args.template.Id);
        if (idx < 0) return;
        _templates[idx] = args.template with { Enabled = args.enabled };
        var projectId = GetParentProject(args.template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await ProjectStore.SaveTemplateAsync(projectId, _templates[idx], CancellationToken.None); }
        catch (Exception ex) { _errorMessage = $"Failed to save: {ex.Message}"; return; }
        if (LoopService.IsLoopActive) { _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id); }
    }

    private async Task ToggleImplementationEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var idx = _templates.FindIndex(t => t.Id == args.template.Id);
        if (idx < 0) return;
        _templates[idx] = args.template with { ImplementationEnabled = args.enabled };
        var projectId = GetParentProject(args.template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await ProjectStore.SaveTemplateAsync(projectId, _templates[idx], CancellationToken.None); }
        catch (Exception ex) { _errorMessage = $"Failed to save: {ex.Message}"; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task ToggleReviewEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var idx = _templates.FindIndex(t => t.Id == args.template.Id);
        if (idx < 0) return;
        _templates[idx] = args.template with { ReviewEnabled = args.enabled };
        var projectId = GetParentProject(args.template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await ProjectStore.SaveTemplateAsync(projectId, _templates[idx], CancellationToken.None); }
        catch (Exception ex) { _errorMessage = $"Failed to save: {ex.Message}"; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task ToggleDecompositionEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var idx = _templates.FindIndex(t => t.Id == args.template.Id);
        if (idx < 0) return;
        _templates[idx] = args.template with { DecompositionEnabled = args.enabled };
        var projectId = GetParentProject(args.template.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        try { await ProjectStore.SaveTemplateAsync(projectId, _templates[idx], CancellationToken.None); }
        catch (Exception ex) { _errorMessage = $"Failed to save: {ex.Message}"; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task AddTemplate()
    {
        _formError = null;
        if (string.IsNullOrWhiteSpace(_addForm.Name)) { _formError = "Name is required."; return; }
        if (string.IsNullOrEmpty(_addForm.IssueProviderId)) { _formError = "Issue Provider is required."; return; }
        if (string.IsNullOrEmpty(_addForm.RepoProviderId)) { _formError = "Repo Provider is required."; return; }
        if (_templates.Any(t => t.IssueProviderId == _addForm.IssueProviderId && t.RepoProviderId == _addForm.RepoProviderId))
        { _formError = "A template with the same Issue Provider + Repo Provider combination already exists."; return; }

        var newTemplate = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(), Name = _addForm.Name.Trim(),
            IssueProviderId = _addForm.IssueProviderId, RepoProviderId = _addForm.RepoProviderId,
            BrainProviderId = string.IsNullOrEmpty(_addForm.BrainProviderId) ? null : _addForm.BrainProviderId,
            PipelineProviderId = string.IsNullOrEmpty(_addForm.PipelineProviderId) ? null : _addForm.PipelineProviderId,
            BrainReadOnly = _addForm.BrainReadOnly, ImplementationEnabled = _addForm.ImplementationEnabled,
            ReviewEnabled = _addForm.ReviewEnabled, DecompositionEnabled = _addForm.DecompositionEnabled, Enabled = true
        };
        var targetProjectId = string.IsNullOrEmpty(_addForm.ProjectId) ? WellKnownIds.DefaultProjectId : _addForm.ProjectId;
        try { await ProjectStore.SaveTemplateAsync(targetProjectId, newTemplate, CancellationToken.None); }
        catch (Exception ex) { _errorMessage = $"Failed to save: {ex.Message}"; return; }
        _templates.Add(newTemplate);
        _projects = await ProjectStore.LoadProjectsAsync(CancellationToken.None);
        _showAddForm = false;
        _successMessage = $"Template \"{newTemplate.Name}\" added.";
        _ = ClearSuccessAfterDelay();
    }

    private async Task RemoveTemplate()
    {
        if (_deletingTemplate == null) return;
        var projectId = GetParentProject(_deletingTemplate.Id)?.Id ?? WellKnownIds.DefaultProjectId;
        _templates.RemoveAll(t => t.Id == _deletingTemplate.Id);
        await ProjectStore.DeleteTemplateAsync(projectId, _deletingTemplate.Id, CancellationToken.None);
        _projects = await ProjectStore.LoadProjectsAsync(CancellationToken.None);
        _showDeleteConfirm = false;
        _successMessage = $"Template \"{_deletingTemplate.Name}\" removed.";
        _deletingTemplate = null;
        _ = ClearSuccessAfterDelay();
    }

    private async Task MoveTemplateToProject((string TemplateId, string SourceProjectId, string TargetProjectId) args)
    {
        try
        {
            var sourceProject = _projects.FirstOrDefault(p => p.Id == args.SourceProjectId);
            var targetProject = _projects.FirstOrDefault(p => p.Id == args.TargetProjectId);
            if (sourceProject == null || targetProject == null) return;
            await ProjectStore.SaveProjectAsync(sourceProject with { TemplateIds = sourceProject.TemplateIds.Where(id => id != args.TemplateId).ToList() }, CancellationToken.None);
            await ProjectStore.SaveProjectAsync(targetProject with { TemplateIds = targetProject.TemplateIds.Append(args.TemplateId).ToList() }, CancellationToken.None);
            _projects = await ProjectStore.LoadProjectsAsync(CancellationToken.None);
            _successMessage = $"Moved \"{_templates.FirstOrDefault(t => t.Id == args.TemplateId)?.Name ?? args.TemplateId}\" to {targetProject.Name}.";
            _ = ClearSuccessAfterDelay();
        }
        catch (Exception ex) { _errorMessage = $"Failed to move template: {ex.Message}"; }
    }

    // ── Loop Controls ──

    private async Task StartLoop()
    {
        _errorMessage = null;
        var started = await LoopService.StartLoopAsync();
        if (!started)
        {
            if (LoopService.ValidationErrors.Count > 0) _errorMessage = "Loop failed to start due to validation errors (see below).";
            else if (LoopService.IsLoopActive) _errorMessage = "Loop is already active.";
            else _errorMessage = "A manual run is in progress. Wait for it to complete.";
        }
    }

    private void StopLoop() => LoopService.StopLoop();
    private void ResumeLoop() => LoopService.ResumeLoop();

    // ── Issue Drawer ──

    private async Task OpenDrawer()
    {
        var template = _templates.FirstOrDefault(t => t.Id == _manualDispatchTemplateId);
        if (template == null) return;
        _drawerTemplate = template; _drawerOpen = true; _drawerPage = 1;
        await LoadDrawerIssues();
    }

    private void CloseDrawer() { _drawerOpen = false; _drawerTemplate = null; _drawerIssues.Clear(); }

    private async Task LoadDrawerIssues()
    {
        if (_drawerTemplate == null) return;
        _drawerLoading = true; StateHasChanged();
        try
        {
            var providerConfig = _issueProviders.FirstOrDefault(p => p.Id == _drawerTemplate.IssueProviderId);
            if (providerConfig == null) { _errorMessage = "Issue provider not found for this template."; _drawerLoading = false; return; }
            await using var provider = ProviderFactory.CreateIssueProvider(providerConfig);
            var result = await provider.ListOpenIssuesAsync(_drawerPage, 25, CancellationToken.None);
            _drawerIssues = result.Items.ToList(); _drawerHasMore = result.HasMore;
        }
        catch (Exception ex) { _errorMessage = $"Failed to load issues: {ex.Message}"; _drawerIssues.Clear(); }
        finally { _drawerLoading = false; }
    }

    private async Task DrawerPrevPage() { if (_drawerPage > 1) { _drawerPage--; await LoadDrawerIssues(); } }
    private async Task DrawerNextPage() { if (_drawerHasMore) { _drawerPage++; await LoadDrawerIssues(); } }

    private async Task DispatchFromDrawer(IssueSummary issue)
    {
        if (_drawerTemplate == null) return;
        if (!_issueProviders.Any(p => p.Id == _drawerTemplate.IssueProviderId) || !_repoProviders.Any(p => p.Id == _drawerTemplate.RepoProviderId))
        { _errorMessage = "Template references providers that no longer exist."; return; }
        if (!JobDispatcher.HasRegisteredAgents) { _errorMessage = "Could not dispatch — no agents are currently connected."; return; }

        var depProviderConfig = _issueProviders.FirstOrDefault(p => p.Id == _drawerTemplate.IssueProviderId);
        if (depProviderConfig != null)
        {
            await using var issueProvider = ProviderFactory.CreateIssueProvider(depProviderConfig);
            var depResult = await DependencyChecker.CheckAsync(issue.Identifier, issue.Description, issueProvider, new Dictionary<int, bool>(), CancellationToken.None);
            if (!depResult.IsReady)
            { _errorMessage = $"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", depResult.BlockedBy.Select(n => $"#{n}"))}"; return; }
        }

        _drawerDispatching = true; StateHasChanged();
        try
        {
            var dispatched = await JobDispatcher.TryDispatchAsync(issue.Identifier, _drawerTemplate.IssueProviderId, _drawerTemplate.RepoProviderId,
                _drawerTemplate.BrainProviderId, _drawerTemplate.PipelineProviderId, initiatedBy: "manual", CancellationToken.None,
                issueTitle: issue.Title, project: GetParentProject(_drawerTemplate.Id));
            if (dispatched) { _successMessage = $"✅ Dispatched #{issue.Identifier}"; CloseDrawer(); _ = ClearSuccessAfterDelay(); }
            else _errorMessage = "Could not dispatch — issue is already being processed or queued, or no agents are available.";
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
        finally { _drawerDispatching = false; }
    }

    // ── PR Drawer ──

    private async Task OpenPrDrawer()
    {
        if (string.IsNullOrEmpty(_manualDispatchTemplateId)) return;
        _prDrawerTemplate = _templates.FirstOrDefault(t => t.Id == _manualDispatchTemplateId);
        if (_prDrawerTemplate == null) return;
        _prDrawerOpen = true; _prDrawerPage = 1;
        await LoadPrDrawerPage();
    }

    private void ClosePrDrawer() => _prDrawerOpen = false;

    private async Task LoadPrDrawerPage()
    {
        if (_prDrawerTemplate == null) return;
        _prDrawerLoading = true; StateHasChanged();
        try
        {
            var repoConfig = _repoProviders.FirstOrDefault(p => p.Id == _prDrawerTemplate.RepoProviderId);
            if (repoConfig == null) { _prDrawerPrs = new(); _prDrawerLoading = false; return; }
            await using var repoProvider = ProviderFactory.CreateRepositoryProvider(repoConfig);
            var result = await repoProvider.ListOpenPullRequestsAsync(_prDrawerPage, 30, null, CancellationToken.None);
            _prDrawerPrs = result.Items.ToList(); _prDrawerHasMore = result.HasMore;
        }
        catch (Exception ex) { _errorMessage = $"Failed to load pull requests: {ex.Message}"; _prDrawerPrs = new(); }
        finally { _prDrawerLoading = false; StateHasChanged(); }
    }

    private async Task PrDrawerNextPage() { _prDrawerPage++; await LoadPrDrawerPage(); }
    private async Task PrDrawerPrevPage() { if (_prDrawerPage > 1) _prDrawerPage--; await LoadPrDrawerPage(); }

    private async Task DispatchPrReviewFromDrawer(PullRequestSummary pr)
    {
        if (_prDrawerTemplate == null) return;
        if (!JobDispatcher.HasRegisteredAgents) { _errorMessage = "Could not dispatch — no agents are currently connected."; return; }
        _prDrawerDispatching = true; StateHasChanged();
        try
        {
            var dispatched = await JobDispatcher.TryDispatchReviewAsync(new ReviewDispatchRequest
            {
                PrIdentifier = pr.Identifier, PrBranchName = pr.BranchName, PrTitle = pr.Title,
                PrDescription = pr.Description, PrAuthor = pr.Author, PrUrl = pr.Url, PrTargetBranch = pr.TargetBranch,
                IssueProviderId = _prDrawerTemplate.IssueProviderId, RepoProviderId = _prDrawerTemplate.RepoProviderId,
                BrainProviderId = _prDrawerTemplate.BrainProviderId, InitiatedBy = "manual"
            }, CancellationToken.None, project: GetParentProject(_prDrawerTemplate.Id));
            if (dispatched) { _successMessage = $"PR #{pr.Identifier} dispatched for review."; _ = ClearSuccessAfterDelay(); }
            else _errorMessage = $"PR #{pr.Identifier} is already being processed or queued.";
        }
        catch (Exception ex) { _errorMessage = $"Failed to dispatch PR review: {ex.Message}"; }
        finally { _prDrawerDispatching = false; StateHasChanged(); }
    }

    // ── Epic Drawer ──

    private async Task OpenEpicDrawer()
    {
        if (string.IsNullOrEmpty(_manualDispatchTemplateId)) return;
        _epicDrawerTemplate = _templates.FirstOrDefault(t => t.Id == _manualDispatchTemplateId);
        if (_epicDrawerTemplate == null) return;
        _epicDrawerOpen = true;
        await LoadEpicDrawerIssues();
    }

    private void CloseEpicDrawer() { _epicDrawerOpen = false; _epicDrawerTemplate = null; _epicDrawerIssues.Clear(); }

    private async Task LoadEpicDrawerIssues()
    {
        if (_epicDrawerTemplate == null) return;
        _epicDrawerLoading = true; StateHasChanged();
        try
        {
            var parentProject = GetParentProject(_epicDrawerTemplate.Id);
            var epicProviderId = !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId) ? parentProject.EpicIssueProviderId : _epicDrawerTemplate.IssueProviderId;
            var providerConfig = _issueProviders.FirstOrDefault(p => p.Id == epicProviderId);
            if (providerConfig == null) { _errorMessage = "Epic issue provider not found."; _epicDrawerLoading = false; return; }
            await using var provider = ProviderFactory.CreateIssueProvider(providerConfig);
            var epicResult = await provider.ListOpenIssuesAsync(1, 50, new[] { "agent:epic" }, CancellationToken.None);
            var approvedResult = await provider.ListOpenIssuesAsync(1, 50, new[] { "agent:epic-approved" }, CancellationToken.None);
            _epicDrawerIssues = epicResult.Items.Concat(approvedResult.Items).ToList();
        }
        catch (Exception ex) { _errorMessage = $"Failed to load epics: {ex.Message}"; _epicDrawerIssues.Clear(); }
        finally { _epicDrawerLoading = false; }
    }

    private async Task DispatchDecompositionFromDrawer(IssueSummary issue)
    {
        if (_epicDrawerTemplate == null) return;
        if (!_issueProviders.Any(p => p.Id == _epicDrawerTemplate.IssueProviderId) || !_repoProviders.Any(p => p.Id == _epicDrawerTemplate.RepoProviderId))
        { _errorMessage = "Template references providers that no longer exist."; return; }
        if (!JobDispatcher.HasRegisteredAgents) { _errorMessage = "Could not dispatch — no agents are currently connected."; return; }
        _epicDrawerDispatching = true; StateHasChanged();
        try
        {
            var phaseType = issue.Labels.Contains("agent:epic-approved", StringComparer.OrdinalIgnoreCase)
                ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
            var dispatched = await JobDispatcher.TryDispatchDecompositionAsync(issue.Identifier, issue.Title, phaseType,
                _epicDrawerTemplate.IssueProviderId, _epicDrawerTemplate.RepoProviderId, _epicDrawerTemplate.BrainProviderId,
                initiatedBy: "manual", CancellationToken.None, project: GetParentProject(_epicDrawerTemplate.Id));
            if (dispatched) { _successMessage = $"✅ Dispatched epic #{issue.Identifier} for {(phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition")}"; CloseEpicDrawer(); _ = ClearSuccessAfterDelay(); }
            else _errorMessage = "Could not dispatch — epic is already being processed or queued, or no agents are available.";
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
        finally { _epicDrawerDispatching = false; }
    }

    // ── Helpers ──

    private PipelineProject? GetParentProject(string templateId) =>
        _projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId));

    private async Task ClearRecentlyToggledAfterDelay(string templateId)
    {
        await Task.Delay(3000);
        _recentlyToggled.Remove(templateId);
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private async Task ClearSuccessAfterDelay()
    {
        await Task.Delay(3000);
        try { await InvokeAsync(() => { if (_disposed) return; _successMessage = null; StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private void DismissAgentSummary() => _showAgentSummary = false;

    private async Task AutoDismissAgentSummary()
    {
        await Task.Delay(8000);
        try { await InvokeAsync(() => { if (_disposed) return; _showAgentSummary = false; StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private async Task AutoDismissLoopToast()
    {
        await Task.Delay(5000);
        try { await InvokeAsync(() => { if (_disposed) return; _hideLoopToast = true; StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    // ── Event Handlers ──

    private async void ElapsedTimerTick(object? state)
    {
        if (_disposed) return;
        var run = PipelineService.ActiveRun;
        if (run is null || run.CurrentStep is PipelineStep.Completed or PipelineStep.Failed or PipelineStep.Cancelled) return;
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private async void HandleStateChanged()
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                var run = PipelineService.ActiveRun;
                if (run != null && run.RunId != _lastRunId) { lock (_outputLock) { _outputLines.Clear(); } _showCompletionOnly = false; _lastRunId = run.RunId; }
                if (run is { CurrentStep: PipelineStep.Completed or PipelineStep.Failed or PipelineStep.Cancelled }) _showCompletionOnly = true;
                var currentStatus = LoopService.StatusMessage;
                if (currentStatus != _lastLoopStatus)
                {
                    _lastLoopStatus = currentStatus;
                    if (currentStatus.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)) _ = AutoDismissLoopToast();
                    else _hideLoopToast = false;
                }
                StateHasChanged();
                try
                {
                    await JS.InvokeVoidAsync(JsScrollToBottom, "outputPanel");
                    await JS.InvokeVoidAsync(JsScrollActiveStep, "sidebarSteps");
                }
                catch (Exception ex) { Log.Debug(ex, "JS interop scroll failed during state change"); }
            });
        }
        catch (ObjectDisposedException) { }
    }

    private async void HandleOutputLine(string line)
    {
        if (_disposed) return;
        lock (_outputLock) { _outputLines.Add(line); }
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                StateHasChanged();
                try { await JS.InvokeVoidAsync(JsScrollToBottom, "outputPanel"); }
                catch (Exception ex) { Log.Debug(ex, "JS interop scroll failed during output"); }
            });
        }
        catch (ObjectDisposedException) { }
    }

    private async Task CancelPipeline()
    {
        try { await PipelineService.CancelPipelineAsync(); }
        catch (Exception ex) { _errorMessage = $"Cancel error: {ex.Message}"; }
    }

    private void BackToIssues() { _showCompletionOnly = false; _errorMessage = null; }

    public void Dispose()
    {
        _disposed = true;
        _elapsedTimer?.Dispose();
        PipelineService.OnChange -= HandleStateChanged;
        PipelineService.OnOutputLine -= HandleOutputLine;
        LoopService.OnChange -= HandleStateChanged;
    }
}
