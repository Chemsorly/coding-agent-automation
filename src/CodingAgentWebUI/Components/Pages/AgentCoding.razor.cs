using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Serilog;

namespace CodingAgentWebUI.Components.Pages;

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
    [Inject] private AgentCodingPageService PageService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [CascadingParameter] private MainLayout? Layout { get; set; }

    private readonly object _outputLock = new();
    private List<string> _outputLines = new();

    private string? _errorMessage;
    private string? _successMessage;
    private bool _showAgentSummary = true;
    private bool _hideLoopToast;
    private string? _lastLoopStatus;
    private bool _showCompletionOnly;
    private string? _lastRunId;
    private bool _disposed;
    private Timer? _elapsedTimer;

    // Template Table UI State
    private bool _showAddForm;
    private TemplateTableSection.TemplateFormModel _addForm = new();
#pragma warning disable CS0414 // Value is read in .razor partial
    private string? _formError;
#pragma warning restore CS0414
    private bool _showDeleteConfirm;
    private PipelineJobTemplate? _deletingTemplate;
    private HashSet<string> _recentlyToggled = new();

    // Manual Dispatch UI State
    private string _manualDispatchTemplateId = "";
    private bool _drawerOpen;
    private PipelineJobTemplate? _drawerTemplate;
    private bool _drawerDispatching;

    // PR Drawer UI State
    private bool _prDrawerOpen;
    private PipelineJobTemplate? _prDrawerTemplate;
    private bool _prDrawerDispatching;

    // Epic Drawer UI State
    private bool _epicDrawerOpen;
    private PipelineJobTemplate? _epicDrawerTemplate;
    private bool _epicDrawerDispatching;

    // ── Delegate properties for .razor template binding ──

    private List<PipelineJobTemplate> _templates => PageService.Templates;
    private IReadOnlyList<PipelineProject> _projects => PageService.Projects;
    private List<ProviderConfig> _issueProviders => PageService.IssueProviders;
    private List<ProviderConfig> _repoProviders => PageService.RepoProviders;
    private List<ProviderConfig> _brainProviders => PageService.BrainProviders;
    private List<ProviderConfig> _pipelineProviders => PageService.PipelineProviders;
    private IReadOnlyList<QualityGateConfiguration> _qualityGateConfigs => PageService.QualityGateConfigs;
    private IReadOnlyList<ReviewerConfiguration> _reviewerConfigs => PageService.ReviewerConfigs;
    private IReadOnlyList<AgentProfile> _agentProfiles => PageService.AgentProfiles;
    private PipelineConfiguration _pipelineConfig => PageService.PipelineConfig;
    private int _maxRetries => PageService.MaxRetries;
    private List<IssueSummary> _drawerIssues => PageService.DrawerIssues;
    private bool _drawerLoading => PageService.DrawerLoading;
    private int _drawerPage => PageService.DrawerPage;
    private bool _drawerHasMore => PageService.DrawerHasMore;
    private Dictionary<string, DependencyCheckResult> _drawerReadiness => PageService.DrawerReadiness;
    private List<PullRequestSummary> _prDrawerPrs => PageService.PrDrawerPrs;
    private bool _prDrawerLoading => PageService.PrDrawerLoading;
    private int _prDrawerPage => PageService.PrDrawerPage;
    private bool _prDrawerHasMore => PageService.PrDrawerHasMore;
    private List<IssueSummary> _epicDrawerIssues => PageService.EpicDrawerIssues;
    private bool _epicDrawerLoading => PageService.EpicDrawerLoading;

    private void OnTemplateChanged(ChangeEventArgs e) =>
        _manualDispatchTemplateId = e.Value?.ToString() ?? "";

    protected override async Task OnInitializedAsync()
    {
        PipelineService.OnChange += HandleStateChanged;
        PipelineService.OnOutputLine += HandleOutputLine;
        LoopService.OnChange += HandleStateChanged;
        _elapsedTimer = new Timer(ElapsedTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        if (Layout is not null)
            Layout.OnEscapePressed += HandleGlobalEscape;

        _errorMessage = await PageService.InitializeAsync();
        _ = AutoDismissAgentSummary();
    }

    // TODO: InvokeAsync(StateHasChanged) is fire-and-forget here; exceptions (e.g. ObjectDisposedException) are silently lost.
    private void HandleGlobalEscape()
    {
        if (_drawerOpen) { CloseDrawer(); InvokeAsync(StateHasChanged); }
        else if (_prDrawerOpen) { ClosePrDrawer(); InvokeAsync(StateHasChanged); }
        else if (_epicDrawerOpen) { CloseEpicDrawer(); InvokeAsync(StateHasChanged); }
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

    // TODO: Original code only added to _recentlyToggled when LoopService.IsLoopActive — behavior change may have unintended side effects
    // TODO: error! uses null-forgiving operator — may pass null to NotificationService.Add if service returns success=false without error message
    private async Task ToggleTemplateEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleTemplateEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task ToggleImplementationEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleImplementationEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task ToggleReviewEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleReviewEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task ToggleDecompositionEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleDecompositionEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
    }

    private async Task AddTemplate()
    {
        _formError = null;
        var (valid, formError) = PageService.ValidateAddTemplate(_addForm);
        if (!valid) { _formError = formError; return; }

        var (success, error, successMessage) = await PageService.AddTemplateAsync(_addForm);
        // TODO: error!/successMessage! use null-forgiving operator — may pass null to NotificationService.Add
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _showAddForm = false;
        _successMessage = successMessage;
        NotificationService.Add(successMessage!, NotificationSeverity.Success);
        _ = ClearSuccessAfterDelay();
    }

    // TODO: error!/successMessage! use null-forgiving operator — may pass null to NotificationService.Add
    private async Task RemoveTemplate()
    {
        if (_deletingTemplate == null) return;
        var (success, error, successMessage) = await PageService.RemoveTemplateAsync(_deletingTemplate);
        if (!success) { _errorMessage = error; NotificationService.Add(error!, NotificationSeverity.Error); return; }
        _showDeleteConfirm = false;
        _successMessage = successMessage;
        NotificationService.Add(successMessage!, NotificationSeverity.Success);
        _deletingTemplate = null;
        _ = ClearSuccessAfterDelay();
    }

    private async Task MoveTemplateToProject((string TemplateId, string SourceProjectId, string TargetProjectId) args)
    {
        var (success, error, successMessage) = await PageService.MoveTemplateToProjectAsync(args.TemplateId, args.SourceProjectId, args.TargetProjectId);
        if (!success) { _errorMessage = error; return; }
        if (successMessage != null) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
    }

    // ── Loop Controls ──

    private async Task StartLoop()
    {
        _errorMessage = null;
        var (success, error) = await PageService.StartLoopAsync();
        if (!success) _errorMessage = error;
    }

    private async Task StopLoop() => await PageService.StopLoopAsync();

    private void ResumeLoop() => PageService.ResumeLoop();

    // ── Issue Drawer ──

    private async Task OpenDrawer()
    {
        var template = _templates.FirstOrDefault(t => t.Id == _manualDispatchTemplateId);
        if (template == null) return;
        _drawerTemplate = template; _drawerOpen = true;
        // TODO: Loading indicator won't render — PageService sets DrawerLoading=true internally but no StateHasChanged() is called before the await, so the UI never shows the spinner. Same issue in OpenPrDrawer and OpenEpicDrawer.
        var error = await PageService.LoadDrawerIssuesAsync(template, 1);
        if (error != null) _errorMessage = error;
        else _ = CheckDrawerDependenciesInBackground(template);
    }

    private void CloseDrawer() { _drawerOpen = false; _drawerTemplate = null; PageService.ClearDrawerIssues(); }

    private async Task DrawerPrevPage()
    {
        if (_drawerPage > 1 && _drawerTemplate != null)
        {
            var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, _drawerPage - 1);
            if (error != null) _errorMessage = error;
            else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
        }
    }

    private async Task DrawerNextPage()
    {
        if (_drawerHasMore && _drawerTemplate != null)
        {
            var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, _drawerPage + 1);
            if (error != null) _errorMessage = error;
            else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
        }
    }

    private async Task DispatchFromDrawer(IssueSummary issue)
    {
        if (_drawerTemplate == null) return;
        _drawerDispatching = true; StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchIssueAsync(issue, _drawerTemplate);
            if (success) { _successMessage = successMessage; CloseDrawer(); _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
        finally { _drawerDispatching = false; }
    }

    // TODO: Add CancellationTokenSource cancelled on drawer close/page change to prevent stale writes and ObjectDisposedException.
    // TODO: Wrap body in try/catch — InvokeAsync can throw ObjectDisposedException if component is disposed during background work.
    private async Task CheckDrawerDependenciesInBackground(PipelineJobTemplate template)
    {
        await PageService.CheckDrawerDependenciesAsync(template, () => InvokeAsync(StateHasChanged));
        await InvokeAsync(StateHasChanged);
    }

    // ── PR Drawer ──

    private async Task OpenPrDrawer()
    {
        if (string.IsNullOrEmpty(_manualDispatchTemplateId)) return;
        _prDrawerTemplate = _templates.FirstOrDefault(t => t.Id == _manualDispatchTemplateId);
        if (_prDrawerTemplate == null) return;
        _prDrawerOpen = true;
        var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    private void ClosePrDrawer() => _prDrawerOpen = false;

    // TODO: Behavioral change — original PrDrawerNextPage always incremented page unconditionally; now guarded by null check on template.
    private async Task PrDrawerNextPage()
    {
        if (_prDrawerTemplate != null)
        {
            var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, _prDrawerPage + 1);
            if (error != null) _errorMessage = error;
        }
    }

    // TODO: Behavioral change — original PrDrawerPrevPage reloaded current page even on page 1; now a no-op on page 1.
    private async Task PrDrawerPrevPage()
    {
        if (_prDrawerPage > 1 && _prDrawerTemplate != null)
        {
            var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, _prDrawerPage - 1);
            if (error != null) _errorMessage = error;
        }
    }

    private async Task DispatchPrReviewFromDrawer(PullRequestSummary pr)
    {
        if (_prDrawerTemplate == null) return;
        _prDrawerDispatching = true; StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchPrReviewAsync(pr, _prDrawerTemplate);
            if (success) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
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
        var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate);
        if (error != null) _errorMessage = error;
    }

    private void CloseEpicDrawer() { _epicDrawerOpen = false; _epicDrawerTemplate = null; PageService.ClearEpicDrawerIssues(); }

    private async Task DispatchDecompositionFromDrawer(IssueSummary issue)
    {
        if (_epicDrawerTemplate == null) return;
        _epicDrawerDispatching = true; StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchDecompositionAsync(issue, _epicDrawerTemplate);
            if (success) { _successMessage = successMessage; CloseEpicDrawer(); _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
        finally { _epicDrawerDispatching = false; }
    }

    // ── Helpers ──

    private PipelineProject? GetParentProject(string templateId) => PageService.GetParentProject(templateId);

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
        if (Layout is not null)
            Layout.OnEscapePressed -= HandleGlobalEscape;
    }
}
