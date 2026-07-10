using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Serilog;

namespace CodingAgentWebUI.Components.Pages;

public partial class AgentCoding : IDisposable
{
    private const string JsScrollToBottom = "scrollToBottom";
    private const string JsScrollActiveStep = "scrollActiveStepIntoView";

    [Inject] private PipelineOrchestrationService PipelineService { get; set; } = default!;
    [Inject] private IPipelineLoopService LoopService { get; set; } = default!;
    [Inject] private OrchestratorRunService RunService { get; set; } = default!;
    [Inject] private IAgentRegistryService Registry { get; set; } = default!;
    [Inject] private JobDispatcherService Dispatcher { get; set; } = default!;
    [Inject] private AgentCodingPageService PageService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] private MainLayout? Layout { get; set; }

    private readonly object _outputLock = new();
    private List<string> _outputLines = [];

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
    private UndoSnackbar _undoSnackbar = default!;

    // Manual Dispatch UI State
    private string _manualDispatchTemplateId = "";
    private bool _drawerOpen => PageService.IsIssueDrawerOpen;
    private PipelineJobTemplate? _drawerTemplate => PageService.IssueDrawerTemplate;
    private bool _drawerDispatching => PageService.IssueDrawerDispatching;

    // PR Drawer UI State
    private bool _prDrawerOpen => PageService.IsPrDrawerOpen;
    private PipelineJobTemplate? _prDrawerTemplate => PageService.PrDrawerTemplate;
    private bool _prDrawerDispatching => PageService.PrDrawerDispatching;

    // Epic Drawer UI State
    private bool _epicDrawerOpen => PageService.IsEpicDrawerOpen;
    private PipelineJobTemplate? _epicDrawerTemplate => PageService.EpicDrawerTemplate;
    private bool _epicDrawerDispatching => PageService.EpicDrawerDispatching;

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
    private Dictionary<string, Pipeline.Models.DependencyCheckResult> _drawerReadiness => PageService.DrawerReadiness;
    private List<string> _drawerLabels => PageService.DrawerLabels;
    private List<string> _drawerSelectedLabels => PageService.DrawerSelectedLabels;
    private List<PullRequestSummary> _prDrawerPrs => PageService.PrDrawerPrs;
    private bool _prDrawerLoading => PageService.PrDrawerLoading;
    private int _prDrawerPage => PageService.PrDrawerPage;
    private bool _prDrawerHasMore => PageService.PrDrawerHasMore;
    private List<string> _prDrawerLabels => PageService.PrDrawerLabels;
    private List<string> _prDrawerSelectedLabels => PageService.PrDrawerSelectedLabels;
    private List<IssueSummary> _epicDrawerIssues => PageService.EpicDrawerIssues;
    private bool _epicDrawerLoading => PageService.EpicDrawerLoading;
    private int _epicDrawerPage => PageService.EpicDrawerPage;
    private bool _epicDrawerHasMore => PageService.EpicDrawerHasMore;
    private List<string> _epicDrawerLabels => PageService.EpicDrawerLabels;
    private List<string> _epicDrawerSelectedLabels => PageService.EpicDrawerSelectedLabels;

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

    private async void HandleGlobalEscape()
    {
        if (_disposed) return;
        PageService.CloseActiveDrawer();
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { }
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

    // TODO: Add bUnit tests verifying _undoSnackbar.Show() is called when LoopService.IsLoopActive == false
    // TODO: Add regression test invoking each toggle with loop inactive to assert snackbar is still shown
    private async Task ToggleTemplateEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleTemplateEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Template {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleTemplateEnabledAsync(current, prev);
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task ToggleImplementationEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleImplementationEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Implementation {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleImplementationEnabledAsync(current, prev);
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task ToggleReviewEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleReviewEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Review {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleReviewEnabledAsync(current, prev);
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task ToggleDecompositionEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleDecompositionEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Decomposition {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleDecompositionEnabledAsync(current, prev);
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task AddTemplate()
    {
        _formError = null;
        var (valid, formError) = PageService.ValidateAddTemplate(_addForm);
        if (!valid) { _formError = formError; return; }

        var (success, error, successMessage) = await PageService.AddTemplateAsync(_addForm);
        if (!success) { _errorMessage = error; return; }
        _showAddForm = false;
        _successMessage = successMessage;
        _ = ClearSuccessAfterDelay();
    }

    private async Task RemoveTemplate()
    {
        if (_deletingTemplate == null) return;
        var (success, error, successMessage) = await PageService.RemoveTemplateAsync(_deletingTemplate);
        if (!success) { _errorMessage = error; return; }
        _showDeleteConfirm = false;
        _successMessage = successMessage;
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

    // TODO: Consider deriving CanStartLoop from `StartLoopDisabledReason == null` to maintain a single source of truth
    // and prevent drift if additional conditions are added in the future.
    // TODO: The Browse buttons use a different disabled condition (no template selected in dropdown) than CanStartLoop
    // (provider/template availability). Per acceptance criteria, Start Loop should also be disabled when Browse is disabled.
    // Evaluate whether these conditions should be aligned.
    private bool CanStartLoop =>
        _templates.Any(t => t.Enabled) &&
        _issueProviders.Count > 0 &&
        _repoProviders.Count > 0;

    private string? StartLoopDisabledReason
    {
        get
        {
            if (!_templates.Any(t => t.Enabled)) return "No enabled pipeline templates configured";
            if (_issueProviders.Count == 0) return "No issue provider configured";
            if (_repoProviders.Count == 0) return "No repository provider configured";
            return null;
        }
    }

    private async Task StartLoop()
    {
        _errorMessage = null;
        try
        {
            var (success, error) = await PageService.StartLoopAsync();
            if (!success) _errorMessage = error;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Failed to start loop: {ex.Message}";
        }
    }

    private async Task StopLoop() => await PageService.StopLoopAsync();

    private void ResumeLoop() => PageService.ResumeLoop();

    // ── Drawer Mutual Exclusion ──

    private string ActiveDrawerTab => PageService.ActiveDrawerTab;

    private PipelineJobTemplate? ActiveDrawerTemplate => PageService.ActiveDrawerTemplate;

    private async Task SwitchToIssueDrawer()
    {
        var error = await PageService.SwitchToIssueDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private async Task SwitchToPrDrawer()
    {
        var error = await PageService.SwitchToPrDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private async Task SwitchToEpicDrawer()
    {
        var error = await PageService.SwitchToEpicDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    // ── Issue Drawer ──

    private async Task OpenDrawer()
    {
        var error = await PageService.OpenIssueDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private void CloseDrawer() => PageService.CloseIssueDrawer();

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

    private async Task DrawerToggleLabel(string label)
    {
        if (_drawerTemplate == null) return;
        PageService.ToggleDrawerLabel(label);
        var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, 1);
        if (error != null) _errorMessage = error;
        else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
    }

    private async Task DrawerClearLabels()
    {
        if (_drawerTemplate == null) return;
        PageService.ClearDrawerLabelFilter();
        var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, 1);
        if (error != null) _errorMessage = error;
        else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
    }

    private async Task CheckDrawerDependenciesInBackground(PipelineJobTemplate template)
    {
        try
        {
            await PageService.CheckDrawerDependenciesAsync(
                template, () => InvokeAsync(StateHasChanged), PageService.DrawerCancellationToken);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

    // NOTE: Setting the dispatching flag before calling the service ensures StateHasChanged
    // renders the spinner/disabled state. The service also sets it (idempotent).
    private async Task DispatchFromDrawer(IssueSummary issue)
    {
        PageService.IssueDrawerDispatching = true;
        StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchFromIssueDrawerAsync(issue);
            if (success) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
    }

    // ── PR Drawer ──

    private async Task OpenPrDrawer()
    {
        var error = await PageService.OpenPrDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private void ClosePrDrawer() => PageService.ClosePrDrawer();

    private async Task PrDrawerNextPage()
    {
        if (_prDrawerTemplate != null)
        {
            var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, _prDrawerPage + 1);
            if (error != null) _errorMessage = error;
        }
    }

    private async Task PrDrawerPrevPage()
    {
        if (_prDrawerPage > 1 && _prDrawerTemplate != null)
        {
            var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, _prDrawerPage - 1);
            if (error != null) _errorMessage = error;
        }
    }

    private async Task PrDrawerToggleLabel(string label)
    {
        if (_prDrawerTemplate == null) return;
        PageService.TogglePrDrawerLabel(label);
        var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    private async Task PrDrawerClearLabels()
    {
        if (_prDrawerTemplate == null) return;
        PageService.ClearPrDrawerLabelFilter();
        var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    // NOTE: Setting the dispatching flag before calling the service ensures StateHasChanged
    // renders the spinner/disabled state. The service also sets it (idempotent).
    private async Task DispatchPrReviewFromDrawer(PullRequestSummary pr)
    {
        PageService.PrDrawerDispatching = true;
        StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchFromPrDrawerAsync(pr);
            if (success) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
        }
        catch (Exception ex) { _errorMessage = $"Failed to dispatch PR review: {ex.Message}"; }
        finally { StateHasChanged(); }
    }

    // ── Epic Drawer ──

    private async Task OpenEpicDrawer()
    {
        var error = await PageService.OpenEpicDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private void CloseEpicDrawer() => PageService.CloseEpicDrawer();

    private async Task EpicDrawerNextPage()
    {
        if (_epicDrawerHasMore && _epicDrawerTemplate != null)
        {
            var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, _epicDrawerPage + 1);
            if (error != null) _errorMessage = error;
        }
    }

    private async Task EpicDrawerPrevPage()
    {
        if (_epicDrawerPage > 1 && _epicDrawerTemplate != null)
        {
            var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, _epicDrawerPage - 1);
            if (error != null) _errorMessage = error;
        }
    }

    private async Task EpicDrawerToggleLabel(string label)
    {
        if (_epicDrawerTemplate == null) return;
        PageService.ToggleEpicDrawerLabel(label);
        var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    private async Task EpicDrawerClearLabels()
    {
        if (_epicDrawerTemplate == null) return;
        PageService.ClearEpicDrawerLabelFilter();
        var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    // NOTE: Setting the dispatching flag before calling the service ensures StateHasChanged
    // renders the spinner/disabled state. The service also sets it (idempotent).
    private async Task DispatchDecompositionFromDrawer(IssueSummary issue)
    {
        PageService.EpicDrawerDispatching = true;
        StateHasChanged();
        try
        {
            var (success, error, successMessage) = await PageService.DispatchFromEpicDrawerAsync(issue);
            if (success) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
            else _errorMessage = error;
        }
        catch (Exception ex) { _errorMessage = $"Dispatch failed: {ex.Message}"; }
    }

    // ── Helpers ──

    private PipelineProject? GetParentProject(string templateId) => PageService.GetParentProject(templateId);

    /// <summary>
    /// Synchronous check against the preloaded active issues set.
    /// Used by drawer component <c>IsBeingProcessed</c> parameter (Func&lt;string, bool&gt;).
    /// </summary>
    private bool IsIssueActive(string issueIdentifier, string issueProviderConfigId)
        => PageService.IsIssueActive(issueIdentifier, issueProviderConfigId);

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

    private void DismissError() => _errorMessage = null;

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
