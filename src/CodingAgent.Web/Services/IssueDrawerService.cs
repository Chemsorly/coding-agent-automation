using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the issue dispatch drawer lifecycle: loading issues, dependency checking,
/// label filtering, pagination, active-issue tracking, and dispatch. The API browses the providers
/// and dispatches (spec 051 2.3b and 2.3c, <see cref="IUiBrowseClient"/> and <see cref="IUiDispatchClient"/>).
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt;.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class IssueDrawerService : IIssueDrawerService, IDisposable
{
    private readonly IUiBrowseClient _browse;
    private readonly IUiDispatchClient _dispatch;

    private readonly DrawerStateService<IssueSummary> _issueDrawer;

    public IssueDrawerService(IUiBrowseClient browse, IUiDispatchClient dispatch)
    {
        _browse = browse;
        _dispatch = dispatch;

        _issueDrawer = new DrawerStateService<IssueSummary>(
            LoadDrawerIssuesCallbackAsync,
            LoadDrawerLabelsCallbackAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception with no indication of the correct call path. Consider returning
            // (false, "Use DispatchFromIssueDrawerAsync", null) instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromIssueDrawerAsync on the coordinator"),
            closeOnDispatch: true,
            postLoadAsync: CheckDrawerDependenciesInBackgroundAsync);
    }

    // ── IIssueDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _issueDrawer;

    public Dictionary<string, DependencyCheckResult> DrawerReadiness { get; private set; } = new();

    /// <summary>
    /// Status-aware map of active issues. Keys are (IssueIdentifier, IssueProviderConfigId) tuples;
    /// values are the WorkItemStatus of the corresponding work item (Pending = Queued, Running/Dispatched = Running).
    /// Populated by <see cref="RefreshActiveIssuesAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId), WorkItemStatus> ActiveIssues { get; private set; } =
        new Dictionary<(IssueIdentifier, ProviderConfigId), WorkItemStatus>();

    // ── Data loading ──

    /// <summary>Private 1-arg callback passed to DrawerStateService (page=1 load).</summary>
    private async Task<string?> LoadDrawerIssuesCallbackAsync(PipelineJobTemplate template)
        => await LoadDrawerIssuesAsync(template, 1);

    public async Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
    {
        _issueDrawer.Loading = true;
        _issueDrawer.Page = page;
        var ct = _issueDrawer.CancellationToken;
        try
        {
            var result = await _browse.GetIssuesAsync(template.Id, _issueDrawer.Page, SelectedLabels(), ct);
            _issueDrawer.Items = result.Items.ToList();
            _issueDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Items.Clear();
            return null;
        }
        // The API's reason is the message to show ("Issue provider not found…", "Failed to load issues: …").
        catch (InvalidOperationException ex) { _issueDrawer.Items.Clear(); return ex.Message; }
        catch (Exception ex) { _issueDrawer.Items.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { _issueDrawer.Loading = false; }
    }

    public Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page)
        => LoadDrawerIssuesAsync(template, page);

    private async Task<string?> LoadDrawerLabelsCallbackAsync(PipelineJobTemplate template)
    {
        var ct = _issueDrawer.CancellationToken;
        try
        {
            _issueDrawer.Labels = (await _browse.GetLabelsAsync(template.Id, ct)).ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
        catch
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
    }

    public Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
        => LoadDrawerLabelsCallbackAsync(template);

    /// <summary>
    /// Fills <see cref="DrawerReadiness"/> for the drawer's page. The API runs the dependency checks;
    /// <paramref name="onProgress"/> is called once the results are in. Best-effort: a failure leaves
    /// the readiness it already has.
    /// </summary>
    public async Task CheckDrawerDependenciesAsync(
        PipelineJobTemplate template,
        Action? onProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await _browse.GetIssueReadinessAsync(template.Id, _issueDrawer.Page, SelectedLabels(), cancellationToken);
            foreach (var (identifier, result) in readiness)
                DrawerReadiness[identifier] = result;
            onProgress?.Invoke();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort: the drawer works without readiness.
        }
    }

    private List<string>? SelectedLabels() =>
        _issueDrawer.SelectedLabels.Count > 0 ? _issueDrawer.SelectedLabels.ToList() : null;

    private async Task CheckDrawerDependenciesInBackgroundAsync(PipelineJobTemplate template, CancellationToken ct)
    {
        try
        {
            await CheckDrawerDependenciesAsync(template, null, ct);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

    public void ClearDrawerIssues()
    {
        _issueDrawer.Items.Clear();
        _issueDrawer.Page = 1;
        _issueDrawer.HasMore = false;
        DrawerReadiness.Clear();
        _issueDrawer.SelectedLabels.Clear();
    }

    // ── Dispatch ──

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(IssueSummary issue, PipelineJobTemplate template)
        => DrawerDispatchHelper.DispatchAsync(() => _dispatch.DispatchIssueAsync(template.Id, issue.Identifier));

    // ── Drawer orchestration ──

    public async Task<string?> OpenIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        // Not the drawer's token: a closed drawer's token is cancelled until OpenAsync renews it.
        await RefreshActiveIssuesAsync(templateId, CancellationToken.None);
        return await _issueDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseIssueDrawer()
    {
        _issueDrawer.Close();
        DrawerReadiness.Clear();
    }

    public Task<string?> SwitchToIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _issueDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _issueDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                await RefreshActiveIssuesAsync(template.Id, CancellationToken.None);
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(IssueSummary issue)
    {
        _issueDrawer.IsDispatching = true;
        try
        {
            if (_issueDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchIssueAsync(issue, _issueDrawer.Template);
            if (success)
                _issueDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _issueDrawer.IsDispatching = false; }
    }

    // ── Active issues ──

    /// <summary>
    /// Reloads <see cref="ActiveIssues"/> for the template's issue tracker, repository and epic tracker:
    /// Pending (queued) when a queued work item exists, otherwise Running (dispatched, running, or
    /// ended within the dedup cooldown). The API computes the badges (spec 051 2.3b).
    /// </summary>
    public async Task RefreshActiveIssuesAsync(TemplateId templateId, CancellationToken ct = default)
    {
        var active = await _browse.GetActiveIssuesAsync(templateId.Value, ct);
        ActiveIssues = active.ToDictionary(
            a => ((IssueIdentifier)a.IssueIdentifier, (ProviderConfigId)a.IssueProviderConfigId),
            a => a.Status);
    }

    /// <summary>
    /// Returns the WorkItemStatus of the issue's current work item, or null if the issue has no
    /// active work item. <see cref="WorkItemStatus.Pending"/> means "Queued";
    /// <see cref="WorkItemStatus.Running"/> means "Running or Dispatched".
    /// </summary>
    public WorkItemStatus? GetIssueWorkItemStatus(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        var key = (issueIdentifier, issueProviderConfigId);
        return ActiveIssues.TryGetValue(key, out var status) ? status : null;
    }

    public bool IsIssueActive(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => ActiveIssues.ContainsKey((issueIdentifier, issueProviderConfigId));

    // ── Cross-drawer coordination ──

    public void Hide() => _issueDrawer.IsOpen = false;

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _issueDrawer.Dispose();
        _disposed = true;
    }
}
