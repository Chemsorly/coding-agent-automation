using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CodingAgentWebUI.UnitTests.Components;

public class DispatchDrawerBaseTests : BunitContext
{
    private sealed record TestItem(string Identifier, string Title);

    private sealed class TestDrawer : DispatchDrawerBase<TestItem>
    {
        public bool CloseCalled { get; private set; }
        public bool DispatchCalled { get; private set; }
        public TestItem? LastDispatched { get; private set; }

        protected override bool MatchesFilter(TestItem item, string filter) =>
            item.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase);

        protected override string GetIdentifier(TestItem item) => item.Identifier;

        protected override async Task DispatchSelected()
        {
            if (SelectedItem != null)
            {
                LastDispatched = SelectedItem;
                DispatchCalled = true;
            }
            await Task.CompletedTask;
        }

        // Expose internals for testing
        public void SetItems(IReadOnlyList<TestItem> items) => Items = items;
        public void SetFilter(string filter) => _filter = filter;
        public void InvokeApplyFilter() => ApplyFilter();
        public void InvokeSelectItem(TestItem item) => SelectItem(item);
        public List<TestItem> GetFilteredItems() => FilteredItems;
        public TestItem? GetSelectedItem() => SelectedItem;
        public int GetHighlightedIndex() => _highlightedIndex;
        public async Task InvokeHandleKeyDown(string key) =>
            await HandleKeyDown(new KeyboardEventArgs { Key = key });

        public void ConfigureCallbacks()
        {
            OnClose = EventCallback.Factory.Create(this, () => CloseCalled = true);
        }

        // Override Close to avoid render handle dependency in tests
        protected override Task Close()
        {
            CloseCalled = true;
            return Task.CompletedTask;
        }
    }

    private TestDrawer CreateDrawer(IReadOnlyList<TestItem> items, bool isOpen = true)
    {
        var drawer = new TestDrawer();
        drawer.SetItems(items);
        drawer.ConfigureCallbacks();
        // Simulate parameter set
        typeof(DispatchDrawerBase<TestItem>).GetProperty(nameof(DispatchDrawerBase<TestItem>.IsOpen))!
            .SetValue(drawer, isOpen);
        drawer.InvokeApplyFilter();
        return drawer;
    }

    [Fact]
    public void ApplyFilter_EmptyFilter_ReturnsAllItems()
    {
        var items = new[] { new TestItem("1", "First"), new TestItem("2", "Second") };
        var drawer = CreateDrawer(items);
        drawer.SetFilter("");
        drawer.InvokeApplyFilter();

        Assert.Equal(2, drawer.GetFilteredItems().Count);
    }

    [Fact]
    public void ApplyFilter_PartialMatch_FiltersCorrectly()
    {
        var items = new[] { new TestItem("1", "First Item"), new TestItem("2", "Second Item") };
        var drawer = CreateDrawer(items);
        drawer.SetFilter("First");
        drawer.InvokeApplyFilter();

        Assert.Single(drawer.GetFilteredItems());
        Assert.Equal("1", drawer.GetFilteredItems()[0].Identifier);
    }

    [Fact]
    public void ApplyFilter_MatchesByIdentifier()
    {
        var items = new[] { new TestItem("123", "Foo"), new TestItem("456", "Bar") };
        var drawer = CreateDrawer(items);
        drawer.SetFilter("123");
        drawer.InvokeApplyFilter();

        Assert.Single(drawer.GetFilteredItems());
        Assert.Equal("123", drawer.GetFilteredItems()[0].Identifier);
    }

    [Fact]
    public void ApplyFilter_CaseInsensitive()
    {
        var items = new[] { new TestItem("1", "Hello World") };
        var drawer = CreateDrawer(items);
        drawer.SetFilter("hello");
        drawer.InvokeApplyFilter();

        Assert.Single(drawer.GetFilteredItems());
    }

    [Fact]
    public void ApplyFilter_NoMatch_ReturnsEmpty()
    {
        var items = new[] { new TestItem("1", "First"), new TestItem("2", "Second") };
        var drawer = CreateDrawer(items);
        drawer.SetFilter("zzz");
        drawer.InvokeApplyFilter();

        Assert.Empty(drawer.GetFilteredItems());
    }

    [Fact]
    public void SelectItem_TogglesSelection()
    {
        var item = new TestItem("1", "Test");
        var drawer = CreateDrawer([item]);

        drawer.InvokeSelectItem(item);
        Assert.Equal(item, drawer.GetSelectedItem());

        drawer.InvokeSelectItem(item);
        Assert.Null(drawer.GetSelectedItem());
    }

    [Fact]
    public void SelectItem_ChangesSelection()
    {
        var item1 = new TestItem("1", "First");
        var item2 = new TestItem("2", "Second");
        var drawer = CreateDrawer([item1, item2]);

        drawer.InvokeSelectItem(item1);
        Assert.Equal(item1, drawer.GetSelectedItem());

        drawer.InvokeSelectItem(item2);
        Assert.Equal(item2, drawer.GetSelectedItem());
    }

    [Fact]
    public async Task HandleKeyDown_ArrowDown_MovesHighlightForward()
    {
        var items = new[] { new TestItem("1", "A"), new TestItem("2", "B"), new TestItem("3", "C") };
        var drawer = CreateDrawer(items);

        await drawer.InvokeHandleKeyDown("ArrowDown");
        Assert.Equal(0, drawer.GetHighlightedIndex());

        await drawer.InvokeHandleKeyDown("ArrowDown");
        Assert.Equal(1, drawer.GetHighlightedIndex());
    }

    [Fact]
    public async Task HandleKeyDown_ArrowUp_MovesHighlightBackward()
    {
        var items = new[] { new TestItem("1", "A"), new TestItem("2", "B"), new TestItem("3", "C") };
        var drawer = CreateDrawer(items);

        // Start at 0
        await drawer.InvokeHandleKeyDown("ArrowDown");
        await drawer.InvokeHandleKeyDown("ArrowDown");
        Assert.Equal(1, drawer.GetHighlightedIndex());

        await drawer.InvokeHandleKeyDown("ArrowUp");
        Assert.Equal(0, drawer.GetHighlightedIndex());
    }

    [Fact]
    public async Task HandleKeyDown_ArrowDown_WrapsAtEnd()
    {
        var items = new[] { new TestItem("1", "A"), new TestItem("2", "B") };
        var drawer = CreateDrawer(items);

        await drawer.InvokeHandleKeyDown("ArrowDown"); // 0
        await drawer.InvokeHandleKeyDown("ArrowDown"); // 1
        await drawer.InvokeHandleKeyDown("ArrowDown"); // wraps to 0
        Assert.Equal(0, drawer.GetHighlightedIndex());
    }

    [Fact]
    public async Task HandleKeyDown_ArrowUp_WrapsAtStart()
    {
        var items = new[] { new TestItem("1", "A"), new TestItem("2", "B"), new TestItem("3", "C") };
        var drawer = CreateDrawer(items);

        // _highlightedIndex starts at -1, ArrowUp should go to last item
        await drawer.InvokeHandleKeyDown("ArrowUp");
        Assert.Equal(2, drawer.GetHighlightedIndex());
    }

    [Fact]
    public async Task HandleKeyDown_Enter_SelectsAndDispatchesHighlightedItem()
    {
        var items = new[] { new TestItem("1", "A"), new TestItem("2", "B") };
        var drawer = CreateDrawer(items);

        await drawer.InvokeHandleKeyDown("ArrowDown"); // highlight index 0
        await drawer.InvokeHandleKeyDown("Enter");

        Assert.True(drawer.DispatchCalled);
        Assert.Equal(-1, drawer.GetHighlightedIndex()); // reset after dispatch
    }

    [Fact]
    public async Task HandleKeyDown_Enter_NoOpWhenNoHighlight()
    {
        var items = new[] { new TestItem("1", "A") };
        var drawer = CreateDrawer(items);

        await drawer.InvokeHandleKeyDown("Enter");

        Assert.False(drawer.DispatchCalled);
    }

    [Fact]
    public async Task HandleKeyDown_Escape_InvokesClose()
    {
        var items = new[] { new TestItem("1", "A") };
        var drawer = CreateDrawer(items);

        await drawer.InvokeHandleKeyDown("Escape");

        Assert.True(drawer.CloseCalled);
    }

    [Fact]
    public async Task ApplyFilter_ResetsHighlightIndex()
    {
        var items = new[] { new TestItem("1", "First"), new TestItem("2", "Second") };
        var drawer = CreateDrawer(items);

        // Manually set highlight
        await drawer.InvokeHandleKeyDown("ArrowDown");
        Assert.Equal(0, drawer.GetHighlightedIndex());

        // Changing filter resets
        drawer.SetFilter("Sec");
        drawer.InvokeApplyFilter();
        Assert.Equal(-1, drawer.GetHighlightedIndex());
    }
}
