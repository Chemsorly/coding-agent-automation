using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the epic dispatch drawer lifecycle: loading epics with deduplication,
/// label filtering, pagination, and decomposition dispatch. The API browses the epic tracker and
/// dispatches (spec 051 2.3b and 2.3c, <see cref="IUiBrowseClient"/> and <see cref="IUiDispatchClient"/>).
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt; for epic items.
/// Registered as Scoped (one instance per browser tab).
/// </summary>
public sealed class EpicDrawerService : IEpicDrawerService, IDisposable
{
    private readonly ILogger<EpicDrawerService> _logger;

    private readonly IUiBrowseClient _browse;
    private readonly IUiDispatchClient _dispatch;

    private readonly DrawerStateService<IssueSummary> _epicDrawer;

    public EpicDrawerService(IUiBrowseClient browse, IUiDispatchClient dispatch, ILogger<EpicDrawerService> logger)
    {
        _logger = logger;
        _browse = browse;
        _dispatch = dispatch;

        _epicDrawer = new DrawerStateService<IssueSummary>(
            t => LoadEpicDrawerIssuesAsync(t, 1),
            LoadEpicDrawerLabelsAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception. Consider returning (false, "Use DispatchFromEpicDrawerAsync", null)
            // instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromEpicDrawerAsync on the coordinator"),
            closeOnDispatch: true);
    }

    // ── IEpicDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _epicDrawer;

    // ── Data loading ──

    /// <summary>
    /// Loads a page of the new and the approved epics. The API lists them from the project's epic
    /// tracker, otherwise the template's own, and merges the two lists (spec 051 2.3b).
    /// </summary>
    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
    {
        _epicDrawer.Loading = true;
        _epicDrawer.Page = page;
        var ct = _epicDrawer.CancellationToken;
        try
        {
            var labels = _epicDrawer.SelectedLabels.Count > 0 ? _epicDrawer.SelectedLabels.ToList() : null;
            var result = await _browse.GetEpicsAsync(template.Id, page, labels, ct);
            _epicDrawer.Items = result.Items.ToList();
            _epicDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _epicDrawer.Items.Clear();
            return null;
        }
        // The API's reason is the message to show ("Epic issue provider not found.", "Failed to load epics: …").
        catch (InvalidOperationException ex) { _epicDrawer.Items.Clear(); return ex.Message; }
        catch (Exception ex) { _epicDrawer.Items.Clear(); return $"Failed to load epics: {ex.Message}"; }
        finally { _epicDrawer.Loading = false; }
    }

    private async Task<string?> LoadEpicDrawerLabelsAsync(PipelineJobTemplate template)
    {
        var ct = _epicDrawer.CancellationToken;
        try
        {
            _epicDrawer.Labels = (await _browse.GetEpicLabelsAsync(template.Id, ct)).ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _epicDrawer.Labels.Clear();
            return null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load epic drawer labels"); _epicDrawer.Labels.Clear(); return null; }
    }

    public void ClearEpicDrawerIssues()
    {
        _epicDrawer.Items.Clear();
        _epicDrawer.Page = 1;
        _epicDrawer.HasMore = false;
        _epicDrawer.SelectedLabels.Clear();
    }

    // ── Dispatch ──

    /// <summary>
    /// Dispatches the epic for analysis, or for decomposition once it is approved. The API binds the run to
    /// the tracker the drawer lists epics from: the project's epic tracker, otherwise the template's own.
    /// </summary>
    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(IssueSummary issue, PipelineJobTemplate template)
        => DrawerDispatchHelper.DispatchAsync(() => _dispatch.DispatchEpicAsync(template.Id, issue.Identifier, CancellationToken.None));

    // ── Drawer orchestration ──

    public async Task<string?> OpenEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        return await _epicDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawer.Close();

    public Task<string?> SwitchToEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _epicDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _epicDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(IssueSummary issue)
    {
        _epicDrawer.IsDispatching = true;
        try
        {
            if (_epicDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchDecompositionAsync(issue, _epicDrawer.Template);
            if (success)
                _epicDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _epicDrawer.IsDispatching = false; }
    }

    // ── Cross-drawer coordination ──

    public void Hide() => _epicDrawer.IsOpen = false;

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _epicDrawer.Dispose();
        _disposed = true;
    }
}
