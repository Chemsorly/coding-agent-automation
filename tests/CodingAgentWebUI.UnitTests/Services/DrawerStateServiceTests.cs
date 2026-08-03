using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

public class DrawerStateServiceTests
{
    private static PipelineJobTemplate MakeTemplate(string id = "t-1", string name = "Test") =>
        new() { Id = id, Name = name, IssueProviderId = "ip-1", RepoProviderId = "rp-1" };

    private static IssueSummary MakeIssue(string id = "42", string title = "Test Issue") =>
        new() { Identifier = id, Title = title, Labels = Array.Empty<string>() };

    private static PullRequestSummary MakePr() =>
        new() { Identifier = "99", Number = 99, Title = "PR", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Description = "", Labels = Array.Empty<string>(), IsDraft = false };

    private static DrawerStateService<IssueSummary> CreateIssueDrawer(
        Func<PipelineJobTemplate, Task<string?>>? loadItems = null,
        Func<PipelineJobTemplate, Task<string?>>? loadLabels = null,
        bool closeOnDispatch = false)
    {
        return new DrawerStateService<IssueSummary>(
            loadItems ?? (_ => Task.FromResult<string?>(null)),
            loadLabels ?? (_ => Task.FromResult<string?>(null)),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, "Dispatched")),
            closeOnDispatch);
    }

    private static DrawerStateService<PullRequestSummary> CreatePrDrawer(bool closeOnDispatch = false)
    {
        return new DrawerStateService<PullRequestSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (p, t) => Task.FromResult<(bool, string?, string?)>((true, null, "Dispatched")),
            closeOnDispatch);
    }

    private static async Task<DrawerStateService<IssueSummary>> CreateAndOpenIssueDrawerAsync(
        bool closeOnDispatch = false)
    {
        var template = MakeTemplate();
        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, "Dispatched")),
            closeOnDispatch);
        await drawer.OpenAsync(template, null);
        return drawer;
    }

    [Fact]
    public async Task OpenAsync_SetsIsOpen_AndLoadsData()
    {
        var template = MakeTemplate();
        var loadedItems = new List<IssueSummary> { MakeIssue("1"), MakeIssue("2") };
        var loadedLabels = new List<string> { "bug", "enhancement" };

        DrawerStateService<IssueSummary>? captured = null;
        var drawer = new DrawerStateService<IssueSummary>(
            _ =>
            {
                captured!.Items = loadedItems;
                captured.HasMore = true;
                return Task.FromResult<string?>(null);
            },
            _ =>
            {
                captured!.Labels = loadedLabels;
                return Task.FromResult<string?>(null);
            },
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, null)));
        captured = drawer;

        var error = await drawer.OpenAsync(template, null);

        Assert.Null(error);
        Assert.True(drawer.IsOpen);
        Assert.Equal(template, drawer.Template);
        Assert.Equal(loadedItems, drawer.Items);
        Assert.True(drawer.HasMore);
        Assert.Equal(loadedLabels, drawer.Labels);
    }

    [Fact]
    public async Task OpenAsync_InvokesNotifyStateChanged()
    {
        var template = MakeTemplate();
        bool notified = false;

        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        var error = await drawer.OpenAsync(template, () => { notified = true; return Task.CompletedTask; });

        Assert.Null(error);
        Assert.True(notified);
        // Verify notifyStateChanged is called AFTER state is set (IsOpen, Template)
        Assert.True(drawer.IsOpen);
        Assert.Equal(template, drawer.Template);
    }

    [Fact]
    public async Task OpenAsync_InvokesPostLoadAsync()
    {
        var template = MakeTemplate();
        var tcs = new TaskCompletionSource<bool>();

        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, null)),
            postLoadAsync: _ => { tcs.SetResult(true); return Task.CompletedTask; });

        var error = await drawer.OpenAsync(template, null);

        Assert.Null(error);
        // Wait for the fire-and-forget postLoadAsync to complete via TCS signal
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(1000)) == tcs.Task;
        Assert.True(completed, "postLoadAsync should be invoked after successful item load");
    }

    [Fact]
    public async Task Close_ClearsState_AndCancelsCts()
    {
        var drawer = await CreateAndOpenIssueDrawerAsync();

        var token = drawer.CancellationToken;
        Assert.False(token.IsCancellationRequested);

        drawer.Close();

        Assert.False(drawer.IsOpen);
        Assert.Null(drawer.Template);
        Assert.Empty(drawer.Items);
        Assert.Equal(1, drawer.Page);
        Assert.False(drawer.HasMore);
        Assert.Empty(drawer.SelectedLabels);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task SwitchAsync_ReusesCache_WhenHasData()
    {
        var template = MakeTemplate();
        var drawer = await CreateAndOpenIssueDrawerAsync();
        drawer.Template = template;
        drawer.Items = new List<IssueSummary> { MakeIssue() };

        // Simulate hidden (IsOpen=false) but data still cached
        drawer.IsOpen = false;

        var error = await drawer.SwitchAsync(
            "t-1", null,
            () => drawer.Items.Count > 0,
            (id, ns) => Task.FromResult<PipelineJobTemplate?>(template));

        Assert.Null(error);
        Assert.True(drawer.IsOpen);
        Assert.Equal(template, drawer.Template);
    }

    [Fact]
    public async Task SwitchAsync_FallsThroughToOpen_WhenNoData()
    {
        bool openCalled = false;
        var template = MakeTemplate();
        var drawer = new DrawerStateService<IssueSummary>(
            _ => { openCalled = true; return Task.FromResult<string?>(null); },
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, null)));

        drawer.IsOpen = false;
        drawer.Template = null;
        drawer.Items.Clear();

        var error = await drawer.SwitchAsync(
            "t-1", null,
            () => drawer.Items.Count > 0,
            (id, ns) => Task.FromResult<PipelineJobTemplate?>(template));

        Assert.Null(error);
        Assert.True(openCalled, "SwitchAsync should call OpenAsync on cache miss");
        Assert.True(drawer.IsOpen);
        Assert.Equal(template, drawer.Template);
    }

    [Fact]
    public async Task DispatchAsync_ClosesOnSuccess_WhenCloseOnDispatchSet()
    {
        bool dispatchCalled = false;
        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (i, t) => { dispatchCalled = true; return Task.FromResult<(bool, string?, string?)>((true, null, "ok")); },
            closeOnDispatch: true);

        drawer.Template = MakeTemplate();
        drawer.IsOpen = true;

        var (success, error, msg) = await drawer.DispatchAsync(MakeIssue(), null);

        Assert.True(success);
        Assert.Equal("ok", msg);
        Assert.True(dispatchCalled);
        Assert.False(drawer.IsOpen);
    }

    [Fact]
    public async Task DispatchAsync_StaysOpenOnSuccess_WhenCloseOnDispatchFalse()
    {
        var drawer = CreatePrDrawer(closeOnDispatch: false);
        drawer.Template = MakeTemplate();
        drawer.IsOpen = true;

        var (success, _, _) = await drawer.DispatchAsync(MakePr(), null);

        Assert.True(success);
        Assert.True(drawer.IsOpen); // stays open
    }

    [Fact]
    public async Task DispatchAsync_ReturnsError_WhenTemplateIsNull()
    {
        var drawer = CreateIssueDrawer(closeOnDispatch: true);

        var (success, error, msg) = await drawer.DispatchAsync(MakeIssue(), null);

        Assert.False(success);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(drawer.IsDispatching); // flag reset
    }

    [Fact]
    public async Task DispatchAsync_SetsAndResetsDispatchingFlag()
    {
        var drawer = CreateIssueDrawer(closeOnDispatch: true);
        drawer.Template = MakeTemplate();

        await drawer.DispatchAsync(MakeIssue(), null);

        Assert.False(drawer.IsDispatching);
    }

    [Fact]
    public async Task DispatchAsync_InvokesNotifyStateChanged_OnSuccessWhenCloseOnDispatch()
    {
        bool notified = false;
        var drawer = CreateIssueDrawer(closeOnDispatch: true);
        drawer.Template = MakeTemplate();
        drawer.IsOpen = true;

        await drawer.DispatchAsync(MakeIssue(), () => { notified = true; return Task.CompletedTask; });

        Assert.True(notified);
    }

    [Fact]
    public async Task DispatchAsync_DoesNotClose_OnFailure()
    {
        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((false, "dispatch failed", null)),
            closeOnDispatch: true);

        drawer.Template = MakeTemplate();
        drawer.IsOpen = true;

        var (success, error, _) = await drawer.DispatchAsync(MakeIssue(), null);

        Assert.False(success);
        Assert.Equal("dispatch failed", error);
        Assert.True(drawer.IsOpen, "Drawer should stay open on failed dispatch even with closeOnDispatch");
    }

    [Fact]
    public async Task CancellationToken_IsValid_WhenDrawerOpen()
    {
        var drawer = await CreateAndOpenIssueDrawerAsync();

        var token = drawer.CancellationToken;
        Assert.NotEqual(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationToken_IsCancelled_AfterDrawerClose()
    {
        var drawer = await CreateAndOpenIssueDrawerAsync();

        var token = drawer.CancellationToken;
        drawer.Close();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationToken_PerDrawerIsolation()
    {
        var drawer1 = await CreateAndOpenIssueDrawerAsync();
        var drawer2 = await CreateAndOpenIssueDrawerAsync();

        var token1 = drawer1.CancellationToken;
        var token2 = drawer2.CancellationToken;

        Assert.NotEqual(CancellationToken.None, token1);
        Assert.NotEqual(CancellationToken.None, token2);

        drawer1.Close();
        Assert.True(token1.IsCancellationRequested);
        Assert.False(token2.IsCancellationRequested);
    }

    [Fact]
    public void ToggleLabel_AddsOnFirstCall_RemovesOnSecondCall()
    {
        var drawer = CreateIssueDrawer();

        drawer.ToggleLabel("bug");

        Assert.Contains("bug", drawer.SelectedLabels);

        drawer.ToggleLabel("bug");

        Assert.DoesNotContain("bug", drawer.SelectedLabels);
    }

    [Fact]
    public void ClearLabelFilter_ClearsSelectedLabels()
    {
        var drawer = CreateIssueDrawer();
        drawer.ToggleLabel("bug");
        drawer.ToggleLabel("enhancement");

        drawer.ClearLabelFilter();

        Assert.Empty(drawer.SelectedLabels);
    }

    [Fact]
    public void ClearItems_ResetsAllState()
    {
        var drawer = CreateIssueDrawer();
        drawer.Items = new List<IssueSummary> { MakeIssue() };
        drawer.Page = 5;
        drawer.HasMore = true;
        drawer.ToggleLabel("bug");

        drawer.ClearItems();

        Assert.Empty(drawer.Items);
        Assert.Equal(1, drawer.Page);
        Assert.False(drawer.HasMore);
        Assert.Empty(drawer.SelectedLabels);
    }

    [Fact]
    public async Task Dispose_CancelsCts()
    {
        var drawer = await CreateAndOpenIssueDrawerAsync();

        var token = drawer.CancellationToken;
        drawer.Dispose();

        Assert.True(token.IsCancellationRequested);

        // double-dispose safe
        drawer.Dispose();
    }

    [Fact]
    public async Task OpenAsync_ReturnsError_WhenLoadItemsFails()
    {
        var template = MakeTemplate();
        bool postLoadCalled = false;

        var drawer = new DrawerStateService<IssueSummary>(
            _ => Task.FromResult<string?>("Failed to load items"),
            _ => Task.FromResult<string?>(null),
            (i, t) => Task.FromResult<(bool, string?, string?)>((true, null, null)),
            postLoadAsync: _ => { postLoadCalled = true; return Task.CompletedTask; });

        var error = await drawer.OpenAsync(template, null);

        Assert.Equal("Failed to load items", error);
        // postLoadAsync should NOT be called when items fail to load
        await Task.Delay(100);
        Assert.False(postLoadCalled, "postLoadAsync should not be invoked when items fail to load");
    }

    [Fact]
    public async Task SwitchAsync_ReturnsNull_WhenResolveTemplateReturnsNull()
    {
        var drawer = CreateIssueDrawer();
        drawer.IsOpen = false;
        drawer.Items.Clear();

        var error = await drawer.SwitchAsync(
            "nonexistent", null,
            () => false,
            (id, ns) => Task.FromResult<PipelineJobTemplate?>(null));

        Assert.Null(error);
        Assert.False(drawer.IsOpen);
    }
}