using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

public class DispatchDrawerBaseTests : BunitContext
{
    private sealed record TestItem(string Identifier, string Title);

    private sealed class TestDrawer : DispatchDrawerBase<TestItem>
    {
        protected override bool MatchesFilter(TestItem item, string filter) =>
            item.Identifier.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase);

        protected override string GetIdentifier(TestItem item) => item.Identifier;

        // Expose internals for testing
        public void SetItems(IReadOnlyList<TestItem> items) => Items = items;
        public void SetFilter(string filter) => _filter = filter;
        public void InvokeApplyFilter() => ApplyFilter();
        public void InvokeSelectItem(TestItem item) => SelectItem(item);
        public List<TestItem> GetFilteredItems() => FilteredItems;
        public TestItem? GetSelectedItem() => SelectedItem;
    }

    private TestDrawer CreateDrawer(IReadOnlyList<TestItem> items, bool isOpen = true)
    {
        var drawer = new TestDrawer();
        drawer.SetItems(items);
        // Simulate parameter set
        typeof(DispatchDrawerBase<TestItem>).GetProperty(nameof(DispatchDrawerBase<TestItem>.IsOpen))!
            .SetValue(drawer, isOpen);
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

    // TODO: Add test for OnParametersSet reset behavior when IsOpen=false (SelectedItem and _filter should reset)
    // TODO: Add tests for PrDispatchDrawer override semantics (non-toggling SelectItem, DispatchSelected nulls SelectedItem)
}
