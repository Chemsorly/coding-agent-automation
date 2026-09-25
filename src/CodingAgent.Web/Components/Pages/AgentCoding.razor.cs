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
    [CascadingParameter] private CockpitLayout? Layout { get; set; }

    /// <summary>
    /// Optional query-string parameter: when set to "issues", the issue dispatch drawer opens
    /// immediately after initialization (if a template is already selected or auto-selected).
    /// Used by the Work page "Browse &amp; dispatch" link via <c>/pipelines?dispatch=issues</c>.
    /// </summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "dispatch")]
    public string? DispatchQueryParam { get; set; }

    private string? _errorMessage;
    private string? _successMessage;
    private bool _showAgentSummary = true;
    private bool _stopPending;
    private bool _disposed;
    // Tracks whether the dispatch-drawer auto-open triggered by DispatchQueryParam has been performed.
    // Reset on each NavigateTo that changes the query string (OnParametersSetAsync is called again).
    private string? _lastAutoOpenDispatchParam;

    // localStorage key used to persist the last-selected manual dispatch template across navigations.
    // This satisfies the acceptance criterion "remembers the last choice when several exist".
    private const string TemplateSelectionStorageKey = "cockpit.dispatch.templateId";

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

    // TODO [WARNING]: OnTemplateChanged is declared async void. If SaveTemplateSelectionAsync throws
    // an exception that is not caught internally (e.g. a JSException subclass not in the catch list,
    // or a future exception added to the method), the exception escapes onto the thread pool and can
    // crash the process. The correct pattern for Blazor @onchange handlers that need await is
    // async Task — Blazor handles the returned Task and surfaces exceptions through its error boundary.
    // Fix: change signature to `private async Task OnTemplateChanged(ChangeEventArgs e)`.
    private async void OnTemplateChanged(ChangeEventArgs e)
    {
        _manualDispatchTemplateId = e.Value?.ToString() ?? "";
        // Persist the selection so it survives navigation away and back (multi-template scenario).
        // Single-template auto-selection also calls this path indirectly via ApplyTemplateAutoSelection
        // but that is handled by SaveTemplateSelectionAsync called after InitializeAsync.
        await SaveTemplateSelectionAsync(_manualDispatchTemplateId);
    }

    protected override async Task OnInitializedAsync()
    {
        LoopService.OnChange += HandleStateChanged;
        if (Layout is not null)
            Layout.OnEscapePressed += HandleGlobalEscape;

        _errorMessage = await PageService.InitializeAsync();

        // Restore the last-selected template from localStorage before auto-selection runs.
        // This ensures that when multiple templates exist and the operator has previously chosen one,
        // ApplyTemplateAutoSelection will find a valid prior selection and keep it (rather than
        // leaving the picker empty and forcing the operator to re-select after every navigation).
        await RestoreTemplateSelectionAsync();

        // Auto-preselect the template if exactly one enabled template is available.
        // This eliminates the mandatory picker step when the operator has only one option.
        // TODO [WARNING]: ApplyTemplateAutoSelection is only called here (on init). If the operator
        // toggles a template's enabled state at runtime via ToggleTemplateEnabled, the auto-selection
        // invariant (single enabled template → preselect) is not re-evaluated. ToggleTemplateEnabled
        // calls StateHasChanged but not ApplyTemplateAutoSelection, so the Browse buttons can remain
        // disabled even when only one template is enabled. Fix: call ApplyTemplateAutoSelection inside
        // ToggleTemplateEnabled after the PageService call succeeds.
        ApplyTemplateAutoSelection();
        // Persist whatever was resolved (handles single-template auto-select case).
        await SaveTemplateSelectionAsync(_manualDispatchTemplateId);

        // If ?dispatch=issues was present and a template is now selected, open the issue drawer.
        // TODO [WARNING]: The _lastAutoOpenDispatchParam flag is set in OnParametersSetAsync (line ~133)
        // unconditionally before the _templates.Count guard, and independently checked here without
        // reading the flag. Under Blazor's standard lifecycle OnInitializedAsync runs before
        // OnParametersSetAsync, so the flag is never set when this branch executes — but if a
        // non-standard host or future framework version reverses the order, the two paths will both
        // set _lastAutoOpenDispatchParam and may conflict. Consider using a single shared flag that
        // both paths read and write to eliminate the ambiguity.
        // TODO [WARNING]: When InitializeAsync succeeds but no template is resolved (all templates
        // disabled, or RestoreTemplateSelectionAsync returned empty), _lastAutoOpenDispatchParam is
        // never set here because the `!string.IsNullOrEmpty(_manualDispatchTemplateId)` guard is not
        // satisfied. On a subsequent navigation away and back to /pipelines?dispatch=issues,
        // OnParametersSetAsync will find _lastAutoOpenDispatchParam != DispatchQueryParam and may open
        // the drawer unexpectedly (if a template has since been enabled). Fix: set the flag
        // unconditionally when DispatchQueryParam == "issues", regardless of whether the drawer was
        // actually opened, so the re-navigation path uses the same logic as the initial visit.
        if (DispatchQueryParam == "issues" && !string.IsNullOrEmpty(_manualDispatchTemplateId))
        {
            _lastAutoOpenDispatchParam = DispatchQueryParam;
            var openError = await PageService.OpenIssueDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
            if (openError != null) _errorMessage = openError;
        }

        _ = AutoDismissAgentSummary();
    }

    protected override async Task OnParametersSetAsync()
    {
        // Handle ?dispatch=issues query parameter: open the issue drawer automatically once
        // per distinct navigate-to. Guard against repeated calls (bUnit renders OnParametersSetAsync
        // multiple times; the _lastAutoOpenDispatchParam flag prevents duplicate opens).
        // TODO [WARNING]: _lastAutoOpenDispatchParam is set here unconditionally before the
        // _templates.Count guard below. If OnParametersSetAsync fires while OnInitializedAsync is
        // still awaiting InitializeAsync (templates not yet loaded), the flag is committed and the
        // fallback path in OnInitializedAsync (which does NOT check the flag) will still open the
        // drawer correctly. However under Blazor's standard lifecycle OnInitializedAsync completes
        // before OnParametersSetAsync is called, so this race does not occur in practice. The two
        // paths should share the same guard variable to eliminate the ambiguity.
        if (DispatchQueryParam == "issues" && _lastAutoOpenDispatchParam != DispatchQueryParam)
        {
            _lastAutoOpenDispatchParam = DispatchQueryParam;
            // Only open if templates have already been loaded (i.e. we're past OnInitializedAsync).
            // If called before initialization is complete the drawer open will be triggered by
            // ApplyTemplateAutoSelection after InitializeAsync, which is correct.
            if (_templates.Count > 0 && !string.IsNullOrEmpty(_manualDispatchTemplateId))
            {
                var error = await PageService.OpenIssueDrawerAsync(_manualDispatchTemplateId, () => InvokeAsync(StateHasChanged));
                if (error != null) _errorMessage = error;
            }
        }
    }

    /// <summary>
    /// Auto-selects the manual dispatch template when exactly one enabled template exists,
    /// or restores the last-used template when multiple exist.
    /// Called after InitializeAsync populates the Templates list (and after RestoreTemplateSelectionAsync
    /// has loaded any persisted selection from localStorage).
    /// </summary>
    private void ApplyTemplateAutoSelection()
    {
        var enabledTemplates = _templates.Where(t => t.Enabled).ToList();
        if (enabledTemplates.Count == 0)
            return;

        if (enabledTemplates.Count == 1)
        {
            // Single enabled template: always preselect it — no choice needed.
            _manualDispatchTemplateId = enabledTemplates[0].Id;
            return;
        }

        // Multiple templates: restore the last selection if it is still valid (enabled).
        // If the previously selected template was disabled/removed, clear the selection.
        if (!string.IsNullOrEmpty(_manualDispatchTemplateId)
            && enabledTemplates.Any(t => t.Id == _manualDispatchTemplateId))
        {
            // Last selection is still valid — keep it.
            return;
        }

        // No prior selection or prior selection no longer valid — leave unset so the operator picks.
        _manualDispatchTemplateId = "";
    }

    /// <summary>
    /// Restores the previously persisted template selection from localStorage.
    /// Only sets <c>_manualDispatchTemplateId</c> when a stored value exists; leaves the field
    /// unchanged (empty) otherwise so <see cref="ApplyTemplateAutoSelection"/> can apply its logic.
    /// Swallows all JS interop exceptions — a missing stored value is not an error.
    /// </summary>
    private async Task RestoreTemplateSelectionAsync()
    {
        try
        {
            var stored = await JS.InvokeAsync<string?>("localStorageGet", CancellationToken.None, TemplateSelectionStorageKey);
            if (!string.IsNullOrEmpty(stored))
                _manualDispatchTemplateId = stored;
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Persists the current template selection to localStorage so it survives navigation.
    /// Passing an empty or null value clears the stored key (operator de-selected the template).
    /// Swallows all JS interop exceptions — persistence is best-effort.
    /// </summary>
    private async Task SaveTemplateSelectionAsync(string? templateId)
    {
        try
        {
            // TODO [WARNING]: JS.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult> binds to
            // an internal BCL type that is not part of the public JSInterop API surface and may be
            // changed or removed in future .NET/Blazor versions. The idiomatic replacement is
            // JS.InvokeVoidAsync(...) which returns ValueTask and is part of the stable public API.
            // Fix: replace with `await JS.InvokeVoidAsync("localStorageSet", CancellationToken.None, TemplateSelectionStorageKey, templateId ?? "");`
            await JS.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "localStorageSet", CancellationToken.None, TemplateSelectionStorageKey, templateId ?? "");
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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