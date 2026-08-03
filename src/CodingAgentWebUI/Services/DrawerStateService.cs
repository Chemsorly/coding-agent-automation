using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Generic service managing the dispatch drawer lifecycle for a single drawer type.
/// Encapsulates state (open/closed, template, items, labels, pagination) and the
/// open/close/switch/dispatch/load/clear lifecycle. Each instance is self-contained
/// with its own CancellationTokenSource.
/// </summary>
public class DrawerStateService<TItem> : IDisposable
{
    private readonly Func<PipelineJobTemplate, Task<string?>> _loadItemsAsync;
    private readonly Func<PipelineJobTemplate, Task<string?>> _loadLabelsAsync;
    private readonly Func<TItem, PipelineJobTemplate, Task<(bool Success, string? Error, string? SuccessMessage)>> _dispatchAsync;
    private readonly bool _closeOnDispatch;
    private readonly Func<PipelineJobTemplate, Task>? _postLoadAsync;

    // ── State ──

    public bool IsOpen { get; set; }
    public PipelineJobTemplate? Template { get; set; }
    public bool IsDispatching { get; set; }
    public List<TItem> Items { get; set; } = new();
    public int Page { get; set; } = 1;
    public bool HasMore { get; set; }
    public bool Loading { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> SelectedLabels { get; private set; } = new();

    // ── Cancellation ──

    private CancellationTokenSource? _cts;
    public CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;

    public DrawerStateService(
        Func<PipelineJobTemplate, Task<string?>> loadItemsAsync,
        Func<PipelineJobTemplate, Task<string?>> loadLabelsAsync,
        Func<TItem, PipelineJobTemplate, Task<(bool Success, string? Error, string? SuccessMessage)>> dispatchAsync,
        bool closeOnDispatch = false,
        Func<PipelineJobTemplate, Task>? postLoadAsync = null)
    {
        _loadItemsAsync = loadItemsAsync;
        _loadLabelsAsync = loadLabelsAsync;
        _dispatchAsync = dispatchAsync;
        _closeOnDispatch = closeOnDispatch;
        _postLoadAsync = postLoadAsync;
    }

    // ── Lifecycle ──

    /// <summary>
    /// Full open lifecycle: set template and open state, reset CTS,
    /// notify caller, load labels + items in parallel, optionally run post-load action.
    /// </summary>
    public async Task<string?> OpenAsync(PipelineJobTemplate template, Func<Task>? notifyStateChanged)
    {
        Template = template;
        IsOpen = true;
        CancelAndResetCts();
        if (notifyStateChanged != null) await notifyStateChanged();
        var labelsTask = _loadLabelsAsync(template);
        var error = await _loadItemsAsync(template);
        await labelsTask;
        if (error != null) return error;
        if (_postLoadAsync != null) _ = _postLoadAsync(template);
        return null;
    }

    /// <summary>
    /// Shared switch lifecycle: evaluate hasData → check cache → reuse or do a full open.
    /// The hasData func is evaluated after HideOtherDrawers (called externally) to avoid stale reads.
    /// </summary>
    public async Task<string?> SwitchAsync(
        string templateId,
        Func<Task>? notifyStateChanged,
        Func<bool> hasDataCache,
        Func<string, Func<Task>?, Task<PipelineJobTemplate?>> resolveTemplateAsync)
    {
        if (Template != null && hasDataCache())
        {
            IsOpen = true;
            return null;
        }
        var template = await resolveTemplateAsync(templateId, notifyStateChanged);
        if (template == null) return null;
        return await OpenAsync(template, notifyStateChanged);
    }

    /// <summary>
    /// Shared dispatch lifecycle: set dispatching flag → guard null template → dispatch →
    /// optionally close on success. Invokes notifyStateChanged after close so the code-behind
    /// can re-render.
    /// </summary>
    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchAsync(
        TItem item, Func<Task>? notifyStateChanged)
    {
        IsDispatching = true;
        try
        {
            if (Template == null) return (false, "No template selected. Please select a template first.", null);
            var (success, error, successMessage) = await _dispatchAsync(item, Template);
            if (success && _closeOnDispatch)
            {
                Close();
                if (notifyStateChanged != null) await notifyStateChanged();
            }
            return (success, error, successMessage);
        }
        finally { IsDispatching = false; }
    }

    // ── Close ──

    /// <summary>Close the drawer, clearing state and cancelling CTS.</summary>
    public void Close()
    {
        IsOpen = false;
        Template = null;
        CancelCts();
        ClearItems();
    }

    // ── Data methods ──

    public void ToggleLabel(string label)
    {
        if (!SelectedLabels.Remove(label))
            SelectedLabels.Add(label);
    }

    public void ClearLabelFilter() => SelectedLabels.Clear();

    public void ClearItems() { Items.Clear(); Page = 1; HasMore = false; SelectedLabels.Clear(); }

    // ── CTS management ──

    private void CancelAndResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void CancelCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            CancelCts();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}