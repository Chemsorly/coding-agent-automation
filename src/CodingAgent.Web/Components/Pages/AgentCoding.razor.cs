using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Components.Layout;
using CodingAgent.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CodingAgent.Web.Components.Pages;

public partial class AgentCoding : IDisposable
{
    [Inject] private ILoopStatusService LoopService { get; set; } = default!;
    [Inject] private IAgentRegistryService Registry { get; set; } = default!;
    [Inject] private AgentCodingPageService PageService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [CascadingParameter] private CockpitLayout? Layout { get; set; }

    /// <summary>
    /// When <c>dispatch=issues</c> is present in the query string, the issue drawer is opened
    /// automatically after the page finishes loading — implementing the "Browse &amp; dispatch"
    /// deep-link from the Work page.
    /// </summary>
    [SupplyParameterFromQuery(Name = "dispatch")]
    public string? DispatchParam { get; set; }

    private const string TemplateStorageKey = "manualDispatch.lastTemplateId";

    private string? _errorMessage;
    private string? _successMessage;
    private bool _showAgentSummary = true;
    private bool _stopPending;
    private bool _disposed;

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

    // Trigger button references for focus-return on drawer close
    private ElementReference _issueDrawerTrigger;
    private ElementReference _prDrawerTrigger;
    private ElementReference _epicDrawerTrigger;

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

    // OnTemplateChanged is async void because Blazor change event handlers cannot return Task.
    // PersistLastTemplateAsync catches all known JS exceptions internally.
    // TODO: [WARNING] TaskCanceledException / OperationCanceledException (e.g. circuit tear-down
    // mid-await) are not caught inside PersistLastTemplateAsync. If either propagates out of the
    // awaited call it escapes the async void and becomes an unobserved exception, potentially
    // crashing the circuit. Add catch (OperationCanceledException) to PersistLastTemplateAsync
    // (or here in the async void body) to close this gap. (DotNetSpecialist, issue #2947)
    private async void OnTemplateChanged(ChangeEventArgs e)
    {
        _manualDispatchTemplateId = e.Value?.ToString() ?? "";
        if (!string.IsNullOrEmpty(_manualDispatchTemplateId))
            await PersistLastTemplateAsync(_manualDispatchTemplateId);
    }

    protected override async Task OnInitializedAsync()
    {
        LoopService.OnChange += HandleStateChanged;
        if (Layout is not null)
            Layout.OnEscapePressed += HandleGlobalEscape;

        _errorMessage = await PageService.InitializeAsync();
        _ = AutoDismissAgentSummary();

        // Restore the last-used template selection from localStorage.
        // Runs after InitializeAsync so _templates is already populated.
        // JS interop throws on pre-render; RestoreLastTemplateAsync catches that silently.
        // On the subsequent interactive render the restore succeeds.
        // TODO: [WARNING] There is no explicit StateHasChanged() after RestoreLastTemplateAsync
        // completes here. Blazor schedules a re-render automatically after OnInitializedAsync
        // finishes, so the dropdown reflects the restored value in practice — but this is an
        // implicit dependency on the Blazor lifecycle. If the lifecycle ever changes (e.g. the
        // restore is moved to a background Task), the UI may not update without an explicit call.
        // (DotNetSpecialist, issue #2947)
        await RestoreLastTemplateAsync();

        // Auto-preselect when exactly one enabled template exists — avoids a required manual
        // pick when only one template is configured. Only applied if no saved value exists.
        if (string.IsNullOrEmpty(_manualDispatchTemplateId))
        {
            var enabled = _templates.Where(t => t.Enabled).ToList();
            if (enabled.Count == 1)
                _manualDispatchTemplateId = enabled[0].Id;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        // Handle ?dispatch=issues deep-link: open the issue drawer automatically.
        // Runs after first render so the interactive circuit is established (drawers require it).
        // NOTE: StateHasChanged() is called only inside the conditional branches, not unconditionally
        // at the end, to avoid the double-render that would occur since Blazor already schedules a
        // re-render after an async OnAfterRenderAsync completes. (DotNetSpecialist warning, #2947)
        if (string.Equals(DispatchParam, "issues", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(_manualDispatchTemplateId))
            {
                var error = await PageService.OpenIssueDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
                if (error != null)
                    _errorMessage = error;
                StateHasChanged();
            }
            else
            {
                // No template is selected (multiple templates configured, no saved preference).
                // Surface feedback so the operator knows they need to pick a template first.
                _errorMessage = "Select a pipeline template to browse and dispatch issues.";
                StateHasChanged();
            }
        }
    }

    // ── Template selection helpers ────────────────────────────────────────

    private async Task RestoreLastTemplateAsync()
    {
        try
        {
            var stored = await JS.InvokeAsync<string?>("localStorageGet", TemplateStorageKey);
            if (!string.IsNullOrEmpty(stored) && _templates.Any(t => t.Id == stored && t.Enabled))
                _manualDispatchTemplateId = stored;
        }
        catch (JSDisconnectedException) { /* circuit gone, skip */ }
        catch (JSException) { /* JS interop unavailable, skip */ }
        catch (ObjectDisposedException) { /* component disposed, skip */ }
        // TODO: [WARNING] InvalidOperationException is a broad base class used throughout .NET and
        // ASP.NET Core for many unrelated failure modes. Catching it silently here will also swallow
        // unrelated InvalidOperationExceptions from JS.InvokeAsync or the _templates.Any() LINQ call,
        // making those bugs invisible. The pre-render JS exception has a predictable message; prefer
        // matching on that message, or restructure to call RestoreLastTemplateAsync only from
        // OnAfterRenderAsync(firstRender: true) where JS interop is always safe — eliminating the
        // need to suppress this exception class entirely. (DotNetSpecialist, issue #2947)
        // Suppresses the Blazor Server pre-render JS interop exception
        // ("JavaScript interop calls cannot be issued at this time").
        catch (InvalidOperationException) { /* pre-render pass, skip */ }
    }

    private async Task PersistLastTemplateAsync(string templateId)
    {
        try
        {
            await JS.InvokeVoidAsync("localStorageSet", TemplateStorageKey, templateId);
        }
        catch (JSDisconnectedException) { /* circuit gone, skip */ }
        catch (JSException) { /* JS interop unavailable, skip */ }
        catch (ObjectDisposedException) { /* component disposed, skip */ }
        catch (InvalidOperationException) { /* pre-render pass, skip */ }
    }

    private async void HandleGlobalEscape()
    {
        if (_disposed) return;
        PageService.CloseActiveDrawer();
        try { await InvokeAsync(StateHasChanged); }
        catch (ObjectDisposedException) { /* Intentional: component disposed before escape key handler ran; no action needed. */ }
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

    private async Task ToggleHousekeepingEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleHousekeepingEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Housekeeping {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleHousekeepingEnabledAsync(current, prev);
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task ToggleBranchCleanupEnabled((PipelineJobTemplate template, bool enabled) args)
    {
        var (success, error) = await PageService.ToggleBranchCleanupEnabledAsync(args.template, args.enabled);
        if (!success) { _errorMessage = error; return; }
        _recentlyToggled.Add(args.template.Id); _ = ClearRecentlyToggledAfterDelay(args.template.Id);
        var prev = !args.enabled;
        var templateId = args.template.Id;
        await _undoSnackbar.Show($"Branch cleanup {(args.enabled ? "enabled" : "disabled")}.", async () =>
        {
            var current = PageService.Templates.FirstOrDefault(t => t.Id == templateId);
            if (current is null) return;
            await PageService.ToggleBranchCleanupEnabledAsync(current, prev);
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

    private async Task MoveTemplateToProject((TemplateId TemplateId, string SourceProjectId, string TargetProjectId) args)
    {
        var (success, error, successMessage) = await PageService.MoveTemplateToProjectAsync(args.TemplateId, args.SourceProjectId, args.TargetProjectId);
        if (!success) { _errorMessage = error; return; }
        if (successMessage != null) { _successMessage = successMessage; _ = ClearSuccessAfterDelay(); }
    }

    // ── Loop Controls ──

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

    private async Task StopLoop()
    {
        // Guard: do not dispatch a second stop request while one is already in progress.
        // The primary prevention mechanism is the disabled button attribute in the razor template,
        // but this guard provides defence-in-depth for programmatic calls (keyboard shortcuts,
        // automated tests, etc.) that bypass the disabled attribute.
        if (_stopPending) return;

        _stopPending = true;
        // TODO: Consider replacing with `await InvokeAsync(StateHasChanged)` for consistency with
        // HandleStateChanged, which always uses InvokeAsync for thread safety. Direct StateHasChanged()
        // is safe here because StopLoop runs on the Blazor render thread (UI event), but the asymmetry
        // is confusing and could silently fail if this method is ever called from a background context.
        StateHasChanged(); // Forces Blazor to re-render before awaiting, disabling the button immediately
        var (success, error) = await PageService.StopLoopAsync();
        if (!success)
        {
            // Stop request failed (scheduler returned error, network failure, etc.).
            // IsLoopActive will remain true — HandleStateChanged will never clear _stopPending.
            // Reset it explicitly here so the button does not stay permanently disabled.
            _stopPending = false;
            _errorMessage = error;
            StateHasChanged();
        }
        // TODO: Add a try/catch around StopLoopAsync to handle unexpected exceptions
        // (e.g. TaskCanceledException during component disposal, ObjectDisposedException).
        // If an unhandled exception escapes this method, _stopPending stays true permanently,
        // leaving the Stop button disabled. StartLoop() above wraps its call in try/catch for
        // exactly this reason — StopLoop() should follow the same pattern. See review finding
        // from DotNetSpecialist (issue #2369).
        // On success: _stopPending stays true until HandleStateChanged sees !IsLoopActive,
        // keeping the button disabled until the poller confirms the loop has actually stopped.
    }

    private async Task ResumeLoop()
    {
        var (success, error) = await PageService.ResumeLoopAsync();
        if (!success) _errorMessage = error;
    }

    // ── Drawer Mutual Exclusion ──

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

    private async Task CloseDrawer()
    {
        PageService.CloseIssueDrawer();
        if (_issueDrawerTrigger.Id != null)
            await _issueDrawerTrigger.FocusAsync();
    }

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
        PageService.IssueDrawer.ToggleLabel(label);
        var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, 1);
        if (error != null) _errorMessage = error;
        else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
    }

    private async Task DrawerClearLabels()
    {
        if (_drawerTemplate == null) return;
        PageService.IssueDrawer.ClearLabelFilter();
        var error = await PageService.LoadDrawerIssuesAsync(_drawerTemplate, 1);
        if (error != null) _errorMessage = error;
        else _ = CheckDrawerDependenciesInBackground(_drawerTemplate);
    }

    private async Task CheckDrawerDependenciesInBackground(PipelineJobTemplate template)
    {
        try
        {
            await PageService.CheckDrawerDependenciesAsync(
                template, () => InvokeAsync(StateHasChanged), PageService.IssueDrawer.CancellationToken);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

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
        finally { StateHasChanged(); }
    }

    // ── PR Drawer ──

    private async Task OpenPrDrawer()
    {
        var error = await PageService.OpenPrDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
        if (error != null) _errorMessage = error;
    }

    private async Task ClosePrDrawer()
    {
        PageService.ClosePrDrawer();
        if (_prDrawerTrigger.Id != null)
            await _prDrawerTrigger.FocusAsync();
    }

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
        PageService.PrDrawer.ToggleLabel(label);
        var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    private async Task PrDrawerClearLabels()
    {
        if (_prDrawerTemplate == null) return;
        PageService.PrDrawer.ClearLabelFilter();
        var error = await PageService.LoadPrDrawerPageAsync(_prDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

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

    private async Task CloseEpicDrawer()
    {
        PageService.CloseEpicDrawer();
        if (_epicDrawerTrigger.Id != null)
            await _epicDrawerTrigger.FocusAsync();
    }

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
        PageService.EpicDrawer.ToggleLabel(label);
        var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

    private async Task EpicDrawerClearLabels()
    {
        if (_epicDrawerTemplate == null) return;
        PageService.EpicDrawer.ClearLabelFilter();
        var error = await PageService.LoadEpicDrawerIssuesAsync(_epicDrawerTemplate, 1);
        if (error != null) _errorMessage = error;
    }

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
        finally { StateHasChanged(); }
    }

    // ── Helpers ──

    private PipelineProject? GetParentProject(TemplateId templateId) => PageService.GetParentProject(templateId);

    /// <summary>
    /// Synchronous check against the preloaded active issues set.
    /// Used by drawer component <c>IsBeingProcessed</c> parameter (Func&lt;string, bool&gt;).
    /// </summary>
    private bool IsIssueActive(string issueIdentifier, string issueProviderConfigId)
        => PageService.IsIssueActive(issueIdentifier, issueProviderConfigId);

    private async Task ClearRecentlyToggledAfterDelay(string templateId)
    {
        await Task.Delay(3000, CancellationToken.None);
        _recentlyToggled.Remove(templateId);
        try { await InvokeAsync(() => { if (!_disposed) StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private async Task ClearSuccessAfterDelay()
    {
        await Task.Delay(3000, CancellationToken.None);
        try { await InvokeAsync(() => { if (_disposed) return; _successMessage = null; StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    private void DismissAgentSummary() => _showAgentSummary = false;

    private void DismissError() => _errorMessage = null;

    private async Task AutoDismissAgentSummary()
    {
        await Task.Delay(8000, CancellationToken.None);
        try { await InvokeAsync(() => { if (_disposed) return; _showAgentSummary = false; StateHasChanged(); }); }
        catch (ObjectDisposedException) { }
    }

    // ── Event Handlers ──

    private async void HandleStateChanged()
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;
                // Clear _stopPending when the poller confirms the loop has stopped.
                // Must be inside InvokeAsync: HandleStateChanged fires on a background thread
                // and state mutations must be marshalled to the Blazor render thread.
                // TODO: There is a subtle race: if the loop stops and immediately restarts between two
                // HandleStateChanged calls, _stopPending is cleared prematurely while a new cycle begins.
                // Also note that _stopPending is cleared for any IsLoopActive=false transition, not only
                // stop-requested ones — this is intentional (natural completion also releases the guard)
                // but should be revisited if a dedicated IsStopPending DTO field is ever added (#2369).
                if (!LoopService.IsLoopActive)
                    _stopPending = false;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        LoopService.OnChange -= HandleStateChanged;
        if (Layout is not null)
            Layout.OnEscapePressed -= HandleGlobalEscape;
        GC.SuppressFinalize(this);
    }
}