using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for DrawerStateService&lt;T&gt;.
/// Covers: open/close lifecycle, dispatch, label selection, cancellation, CTS reset on re-open.
/// </summary>
public sealed class DrawerStateServiceTests : IDisposable
{
    private static PipelineJobTemplate MakeTemplate(string id = "t1") =>
        new() { Id = id, Name = "Test Template", IssueProviderId = "github", RepoProviderId = "github-repo" };

    private static DrawerStateService<string> Create(
        Func<PipelineJobTemplate, Task<string?>>? loadItems = null,
        Func<PipelineJobTemplate, Task<string?>>? loadLabels = null,
        Func<string, PipelineJobTemplate, Task<(bool, string?, string?)>>? dispatch = null,
        bool closeOnDispatch = false)
    {
        return new DrawerStateService<string>(
            loadItems ?? (_ => Task.FromResult<string?>(null)),
            loadLabels ?? (_ => Task.FromResult<string?>(null)),
            dispatch ?? ((_, _) => Task.FromResult<(bool, string?, string?)>((true, null, "dispatched"))),
            closeOnDispatch);
    }

    private readonly DrawerStateService<string> _sut = Create();

    public void Dispose() => _sut.Dispose();

    // ── Initial state ─────────────────────────────────────────────────────

    [Fact]
    public void InitialState_IsClosedAndEmpty()
    {
        _sut.IsOpen.Should().BeFalse();
        _sut.Template.Should().BeNull();
        _sut.Items.Should().BeEmpty();
        _sut.Labels.Should().BeEmpty();
        _sut.IsDispatching.Should().BeFalse();
        _sut.Page.Should().Be(1);
        _sut.HasMore.Should().BeFalse();
    }

    [Fact]
    public void InitialState_CancellationToken_IsValid()
    {
        // CTS pre-created at construction — token must be usable before first open
        _sut.CancellationToken.Should().NotBe(CancellationToken.None);
    }

    // ── Open ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_SetsIsOpenAndTemplate()
    {
        var template = MakeTemplate();
        await _sut.OpenAsync(template, null);

        _sut.IsOpen.Should().BeTrue();
        _sut.Template.Should().Be(template);
    }

    [Fact]
    public async Task OpenAsync_WhenLoadItemsReturnsError_ReturnsError()
    {
        using var sut = Create(loadItems: _ => Task.FromResult<string?>("load error"));
        var error = await sut.OpenAsync(MakeTemplate(), null);
        error.Should().Be("load error");
    }

    [Fact]
    public async Task OpenAsync_WhenLoadSucceeds_ReturnsNull()
    {
        var error = await _sut.OpenAsync(MakeTemplate(), null);
        error.Should().BeNull();
    }

    [Fact]
    public async Task OpenAsync_ResetsCancellationToken()
    {
        var tokenBefore = _sut.CancellationToken;
        await _sut.OpenAsync(MakeTemplate(), null);
        var tokenAfter = _sut.CancellationToken;

        // A new CTS was created — the token itself is different
        tokenBefore.Should().NotBe(tokenAfter);
    }

    [Fact]
    public async Task OpenAsync_InvokesNotifyStateChanged()
    {
        var notified = false;
        await _sut.OpenAsync(MakeTemplate(), () => { notified = true; return Task.CompletedTask; });
        notified.Should().BeTrue();
    }

    // ── Close ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Close_SetsIsOpenFalseAndClearsTemplate()
    {
        await _sut.OpenAsync(MakeTemplate(), null);
        _sut.Close();

        _sut.IsOpen.Should().BeFalse();
        _sut.Template.Should().BeNull();
    }

    [Fact]
    public async Task Close_ClearsItems()
    {
        await _sut.OpenAsync(MakeTemplate(), null);
        _sut.Items.Add("item-1");
        _sut.Close();

        _sut.Items.Should().BeEmpty();
        _sut.Page.Should().Be(1);
    }

    [Fact]
    public async Task Close_ClearsSelectedLabels()
    {
        await _sut.OpenAsync(MakeTemplate(), null);
        _sut.ToggleLabel("kiro");
        _sut.Close();

        _sut.SelectedLabels.Should().BeEmpty();
    }

    // ── Dispatch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchAsync_WhenNoTemplate_ReturnsError()
    {
        var (success, error, _) = await _sut.DispatchAsync("item", null);
        success.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public async Task DispatchAsync_WhenTemplateSet_InvokesDispatchDelegate()
    {
        var dispatched = false;
        using var sut = Create(dispatch: (_, _) =>
        {
            dispatched = true;
            return Task.FromResult<(bool, string?, string?)>((true, null, "ok"));
        });
        await sut.OpenAsync(MakeTemplate(), null);

        await sut.DispatchAsync("item", null);
        dispatched.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_IsDispatchingFalseAfterCompletion()
    {
        await _sut.OpenAsync(MakeTemplate(), null);
        await _sut.DispatchAsync("item", null);
        _sut.IsDispatching.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_IsDispatchingFalseEvenOnException()
    {
        using var sut = Create(dispatch: (_, _) => throw new InvalidOperationException("dispatch failed"));
        await sut.OpenAsync(MakeTemplate(), null);

        var act = () => sut.DispatchAsync("item", null);
        await act.Should().ThrowAsync<InvalidOperationException>();
        sut.IsDispatching.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WhenCloseOnDispatch_ClosesDrawerOnSuccess()
    {
        using var sut = Create(closeOnDispatch: true);
        await sut.OpenAsync(MakeTemplate(), null);

        await sut.DispatchAsync("item", null);
        sut.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WhenCloseOnDispatch_DoesNotCloseOnFailure()
    {
        using var sut = Create(
            closeOnDispatch: true,
            dispatch: (_, _) => Task.FromResult<(bool, string?, string?)>((false, "error", null)));
        await sut.OpenAsync(MakeTemplate(), null);

        await sut.DispatchAsync("item", null);
        sut.IsOpen.Should().BeTrue();
    }

    // ── Label management ──────────────────────────────────────────────────

    [Fact]
    public void ToggleLabel_AddsLabelIfNotPresent()
    {
        _sut.ToggleLabel("kiro");
        _sut.SelectedLabels.Should().Contain("kiro");
    }

    [Fact]
    public void ToggleLabel_RemovesLabelIfPresent()
    {
        _sut.ToggleLabel("kiro");
        _sut.ToggleLabel("kiro");
        _sut.SelectedLabels.Should().NotContain("kiro");
    }

    [Fact]
    public void ToggleLabel_MultipleLabels()
    {
        _sut.ToggleLabel("kiro");
        _sut.ToggleLabel("dotnet");
        _sut.SelectedLabels.Should().HaveCount(2);
    }

    [Fact]
    public void ClearLabelFilter_RemovesAllSelectedLabels()
    {
        _sut.ToggleLabel("kiro");
        _sut.ToggleLabel("dotnet");
        _sut.ClearLabelFilter();
        _sut.SelectedLabels.Should().BeEmpty();
    }

    // ── ClearItems ────────────────────────────────────────────────────────

    [Fact]
    public void ClearItems_ResetsPageToOne()
    {
        _sut.Page = 5;
        _sut.ClearItems();
        _sut.Page.Should().Be(1);
    }

    [Fact]
    public void ClearItems_SetsHasMoreFalse()
    {
        _sut.HasMore = true;
        _sut.ClearItems();
        _sut.HasMore.Should().BeFalse();
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sut = Create();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_IdempotentDoubleDispose()
    {
        var sut = Create();
        sut.Dispose();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }
}
