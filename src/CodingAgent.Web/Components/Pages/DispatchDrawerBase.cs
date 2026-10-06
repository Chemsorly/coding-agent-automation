using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Components.Pages;

/// <summary>
/// Base class for dispatch drawer components that share filter, selection, and lifecycle logic.
/// </summary>
public abstract class DispatchDrawerBase<TItem> : ComponentBase where TItem : class
{
    [Parameter, EditorRequired] public bool IsOpen { get; set; }
    [Parameter, EditorRequired] public PipelineJobTemplate? Template { get; set; }
    [Parameter, EditorRequired] public bool IsLoading { get; set; }
    [Parameter, EditorRequired] public bool IsDispatching { get; set; }
    // TODO: EditorRequired with a default initializer may generate a compiler warning. Consider removing the default or the attribute.
    /// <summary>
    /// Returns the WorkItemStatus for the given issue identifier, or null if no active work item exists.
    /// <see cref="WorkItemStatus.Pending"/> = Queued; <see cref="WorkItemStatus.Running"/> or
    /// <see cref="WorkItemStatus.Dispatched"/> = Running.
    /// </summary>
    [Parameter, EditorRequired] public Func<string, WorkItemStatus?> GetProcessingStatus { get; set; } = _ => null;
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<TItem> OnDispatch { get; set; }

    /// <summary>
    /// Spec 049: whether the signed-in user may dispatch from this drawer (operator on the
    /// template's project). False hides the dispatch button; the page service enforces the rule.
    /// </summary>
    [Parameter] public bool CanDispatch { get; set; } = true;
    [Parameter] public RenderFragment? HeaderPrefix { get; set; }

    protected string _filter = "";
    protected List<TItem> FilteredItems = [];
    protected TItem? SelectedItem;
    protected int _highlightedIndex = -1;
    protected IReadOnlyList<TItem> Items { get; set; } = [];

    // TODO: OnParametersSet allocates a new filtered list on every parent re-render because the
    // parent passes a mutable List<T> reference. Consider caching or using ShouldRender override.
    protected override void OnParametersSet()
    {
        // The parent re-renders on agent and status updates. Keep the keyboard highlight on the
        // same item across those re-renders; only a filter change resets it.
        var highlightedId = _highlightedIndex >= 0 && _highlightedIndex < FilteredItems.Count
            ? GetIdentifier(FilteredItems[_highlightedIndex])
            : null;
        ApplyFilter();
        if (highlightedId is not null)
            _highlightedIndex = FilteredItems.FindIndex(i => GetIdentifier(i) == highlightedId);
        if (!IsOpen) { SelectedItem = default; _filter = ""; _highlightedIndex = -1; }
    }

    protected void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(_filter))
        {
            FilteredItems = Items.ToList();
        }
        else
        {
            var f = _filter.Trim();
            FilteredItems = Items.Where(i => MatchesFilter(i, f)).ToList();
        }
        // Reset highlight when filter changes
        _highlightedIndex = -1;
    }

    protected abstract bool MatchesFilter(TItem item, string filter);

    protected virtual void SelectItem(TItem item)
    {
        if (SelectedItem != null && GetIdentifier(SelectedItem) == GetIdentifier(item))
            SelectedItem = default;
        else
            SelectedItem = item;
    }

    // TODO: async virtual method may emit CS1998 if derived classes override without awaiting. Consider splitting into non-async guard + async invoke.
    protected virtual async Task DispatchSelected()
    {
        // Enter in the list dispatches too, so the check is here and not only on the button.
        if (SelectedItem is not null && CanDispatch)
            await OnDispatch.InvokeAsync(SelectedItem);
    }

    protected abstract string GetIdentifier(TItem item);

    // Made virtual to allow override in tests and derived classes.
    protected virtual async Task Close() => await OnClose.InvokeAsync();

    protected async Task HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                if (FilteredItems.Count > 0)
                    _highlightedIndex = (_highlightedIndex + 1) % FilteredItems.Count;
                break;
            case "ArrowUp":
                if (FilteredItems.Count > 0)
                    _highlightedIndex = _highlightedIndex <= 0 ? FilteredItems.Count - 1 : _highlightedIndex - 1;
                break;
            case "Enter":
                if (_highlightedIndex >= 0 && _highlightedIndex < FilteredItems.Count)
                {
                    var item = FilteredItems[_highlightedIndex];
                    SelectItem(item);
                    await DispatchSelected();
                    _highlightedIndex = -1;
                }
                break;
            case "Escape":
                await Close();
                break;
        }
    }

    protected string GetHighlightClass(int index) =>
        index == _highlightedIndex ? "drawer-item-highlighted" : "";
}
