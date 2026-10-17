using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the PR review dispatch drawer lifecycle: loading pull requests,
/// label filtering, pagination, and dispatch. The API browses the repository and dispatches
/// (spec 051 2.3b and 2.3c, <see cref="IUiBrowseClient"/> and <see cref="IUiDispatchClient"/>).
/// Owns and constructs the underlying DrawerStateService&lt;PullRequestSummary&gt;.
/// Registered as Scoped (one instance per browser tab).
/// </summary>
public sealed class PrReviewDrawerService : IPrReviewDrawerService, IDisposable
{
    private readonly ILogger<PrReviewDrawerService> _logger;

    private readonly IUiBrowseClient _browse;
    private readonly IUiDispatchClient _dispatch;

    private readonly DrawerStateService<PullRequestSummary> _prDrawer;

    public PrReviewDrawerService(IUiBrowseClient browse, IUiDispatchClient dispatch, ILogger<PrReviewDrawerService> logger)
    {
        _logger = logger;
        _browse = browse;
        _dispatch = dispatch;

        _prDrawer = new DrawerStateService<PullRequestSummary>(
            t => LoadPrDrawerPageAsync(t, 1),
            LoadPrDrawerLabelsAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception. Consider returning (false, "Use DispatchFromPrDrawerAsync", null)
            // instead of throwing.
            (pr, template) => throw new InvalidOperationException("Use DispatchFromPrDrawerAsync on the coordinator"));
    }

    // ── IPrReviewDrawerService ──

    public DrawerStateService<PullRequestSummary> DrawerState => _prDrawer;

    // ── Data loading ──

    public async Task<string?> LoadPrDrawerPageAsync(PipelineJobTemplate template, int page)
    {
        _prDrawer.Loading = true;
        _prDrawer.Page = page;
        var ct = _prDrawer.CancellationToken;
        try
        {
            var labels = _prDrawer.SelectedLabels.Count > 0 ? _prDrawer.SelectedLabels.ToList() : null;
            var result = await _browse.GetPullRequestsAsync(template.Id, page, labels, ct);
            _prDrawer.Items = result.Items.ToList();
            _prDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _prDrawer.Items = new();
            return null;
        }
        // The API's reason is the message to show ("Repository provider not found…", "Failed to load pull requests: …").
        catch (InvalidOperationException ex) { _prDrawer.Items = new(); return ex.Message; }
        catch (Exception ex) { _prDrawer.Items = new(); return $"Failed to load pull requests: {ex.Message}"; }
        finally { _prDrawer.Loading = false; }
    }

    /// <summary>The PR drawer filters by the labels of the template's issue tracker, as the issue drawer does.</summary>
    private async Task<string?> LoadPrDrawerLabelsAsync(PipelineJobTemplate template)
    {
        var ct = _prDrawer.CancellationToken;
        try
        {
            _prDrawer.Labels = (await _browse.GetLabelsAsync(template.Id, ct)).ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _prDrawer.Labels.Clear();
            return null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load PR drawer labels"); _prDrawer.Labels.Clear(); return null; }
    }

    public void ClearPrDrawerLabelFilter()
    {
        // TODO: [WARNING] This delegates entirely to DrawerStateService.ClearItems(). Verify that
        // ClearItems() resets Page, HasMore, and SelectedLabels in addition to clearing Items. The
        // old implementation explicitly reset all four fields. If ClearItems() only clears Items, the
        // Page, HasMore, and SelectedLabels state will be stale after a label filter clear, causing
        // pagination to resume from a non-1 page on the next load. The
        // PrReviewDrawerServiceTests.ClearPrDrawerLabelFilter_ClearsItemsAndPage test guards Page and
        // HasMore but does not assert SelectedLabels.Clear().
        _prDrawer.ClearItems();
    }

    // ── Dispatch ──

    public Task<(bool Success, string? Error, string? SuccessMessage)> DispatchPrReviewAsync(PullRequestSummary pr, PipelineJobTemplate template)
        => DrawerDispatchHelper.DispatchAsync(() => _dispatch.DispatchPullRequestAsync(template.Id, pr.Number, CancellationToken.None));

    // ── Drawer orchestration ──

    public async Task<string?> OpenPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        return await _prDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void ClosePrDrawer() => _prDrawer.Close();

    public Task<string?> SwitchToPrDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _prDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _prDrawer.Items.Count > 0 && _prDrawer.Template?.Id == templateId.Value,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromPrDrawerAsync(PullRequestSummary pr)
    {
        _prDrawer.IsDispatching = true;
        try
        {
            if (_prDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            return await DispatchPrReviewAsync(pr, _prDrawer.Template);
        }
        finally { _prDrawer.IsDispatching = false; }
    }

    // ── Cross-drawer coordination ──

    public void Hide() => _prDrawer.IsOpen = false;

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _prDrawer.Dispose();
        _disposed = true;
    }
}
