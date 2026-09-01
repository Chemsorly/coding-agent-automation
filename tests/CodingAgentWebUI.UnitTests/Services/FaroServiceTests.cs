using CodingAgentWebUI.Services;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

public class FaroServiceTests
{
    private static (FaroService sut, Mock<IJSRuntime> jsMock) Create()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                It.IsAny<string>(), It.IsAny<object?[]?>()))
            .Returns(new ValueTask<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>()));
        return (new FaroService(js.Object), js);
    }

    // ── PushLogAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PushLogAsync_InvokesJsInterop_WithCorrectFunctionAndMessage()
    {
        var (sut, js) = Create();

        await sut.PushLogAsync("hello faro");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.Is<object?[]?>(args => args != null && (string)args[0]! == "hello faro")),
            Times.Once);
    }

    [Fact]
    public async Task PushLogAsync_DefaultLevel_IsInfo()
    {
        var (sut, js) = Create();

        await sut.PushLogAsync("msg");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.Is<object?[]?>(args => args != null && (string)args[1]! == "info")),
            Times.Once);
    }

    [Fact]
    public async Task PushLogAsync_CustomLevel_IsForwarded()
    {
        var (sut, js) = Create();

        await sut.PushLogAsync("warn msg", "warn");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.Is<object?[]?>(args => args != null && (string)args[1]! == "warn")),
            Times.Once);
    }

    // ── PushErrorAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PushErrorAsync_InvokesJsInterop_WithCorrectFunctionAndMessage()
    {
        var (sut, js) = Create();

        await sut.PushErrorAsync("something broke");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.Is<object?[]?>(args => args != null && (string)args[0]! == "something broke")),
            Times.Once);
    }

    [Fact]
    public async Task PushErrorAsync_WithStack_ForwardsStack()
    {
        var (sut, js) = Create();

        await sut.PushErrorAsync("oops", "at Foo() line 42");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.Is<object?[]?>(args => args != null && (string)args[1]! == "at Foo() line 42")),
            Times.Once);
    }

    [Fact]
    public async Task PushErrorAsync_WithoutStack_PassesNullStack()
    {
        var (sut, js) = Create();

        await sut.PushErrorAsync("oops");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.Is<object?[]?>(args => args != null && args[1] == null)),
            Times.Once);
    }

    // ── PushEventAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PushEventAsync_InvokesJsInterop_WithCorrectFunctionAndName()
    {
        var (sut, js) = Create();

        await sut.PushEventAsync("pipeline.dispatched");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushEvent",
            It.Is<object?[]?>(args => args != null && (string)args[0]! == "pipeline.dispatched")),
            Times.Once);
    }

    [Fact]
    public async Task PushEventAsync_WithAttributes_ForwardsAttributes()
    {
        var (sut, js) = Create();
        var attrs = new Dictionary<string, string> { ["issue"] = "42", ["project"] = "my-project" };

        await sut.PushEventAsync("pipeline.dispatched", attrs);

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushEvent",
            It.Is<object?[]?>(args =>
                args != null &&
                args[1] is Dictionary<string, string> &&
                ((Dictionary<string, string>)args[1]!)["issue"] == "42" &&
                ((Dictionary<string, string>)args[1]!)["project"] == "my-project")),
            Times.Once);
    }

    [Fact]
    public async Task PushEventAsync_WithoutAttributes_PassesNullAttributes()
    {
        var (sut, js) = Create();

        await sut.PushEventAsync("some.event");

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushEvent",
            It.Is<object?[]?>(args => args != null && args[1] == null)),
            Times.Once);
    }

    // ── Graceful degradation ─────────────────────────────────────────────────
    // All three methods share one catch predicate (IsSafeToSwallow), so a single
    // parameterized Theory catches per-method catch-block regressions while
    // eliminating three structurally identical copies.

    [Theory]
    [InlineData("PushLog",   "JSException")]
    [InlineData("PushLog",   "JSDisconnectedException")]
    [InlineData("PushLog",   "OperationCanceledException")]
    [InlineData("PushLog",   "ObjectDisposedException")]
    [InlineData("PushLog",   "InvalidOperationException")]
    [InlineData("PushError", "JSException")]
    [InlineData("PushError", "JSDisconnectedException")]
    [InlineData("PushError", "OperationCanceledException")]
    [InlineData("PushError", "ObjectDisposedException")]
    [InlineData("PushError", "InvalidOperationException")]
    [InlineData("PushEvent", "JSException")]
    [InlineData("PushEvent", "JSDisconnectedException")]
    [InlineData("PushEvent", "OperationCanceledException")]
    [InlineData("PushEvent", "ObjectDisposedException")]
    [InlineData("PushEvent", "InvalidOperationException")]
    public async Task DoesNotThrow_ForAllSafeExceptionTypes(string method, string exceptionType)
    {
        var js = MakeThrowingMock(exceptionType);
        var sut = new FaroService(js.Object);

        var ex = await Record.ExceptionAsync(() => method switch
        {
            "PushLog"   => sut.PushLogAsync("test"),
            "PushError" => sut.PushErrorAsync("test"),
            "PushEvent" => sut.PushEventAsync("test"),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        });

        Assert.Null(ex);
    }

    // ── Negative: filter is selective (unexpected exceptions must propagate) ──
    // Kept as three named Facts so a regression in any specific method's try/catch
    // is clearly identified in the test runner output.

    [Fact]
    public async Task PushLogAsync_Propagates_WhenUnexpectedExceptionOccurs()
    {
        // Verifies the filter is selective — swallowing ALL exceptions would be wrong.
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                It.IsAny<string>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new ArgumentNullException("param"));

        var sut = new FaroService(js.Object);

        var ex = await Record.ExceptionAsync(() => sut.PushLogAsync("test"));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public async Task PushErrorAsync_Propagates_WhenUnexpectedExceptionOccurs()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                It.IsAny<string>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new ArgumentNullException("param"));

        var sut = new FaroService(js.Object);

        var ex = await Record.ExceptionAsync(() => sut.PushErrorAsync("test"));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public async Task PushEventAsync_Propagates_WhenUnexpectedExceptionOccurs()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                It.IsAny<string>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(new ArgumentNullException("param"));

        var sut = new FaroService(js.Object);

        var ex = await Record.ExceptionAsync(() => sut.PushEventAsync("test"));
        Assert.IsType<ArgumentNullException>(ex);
    }

    private static Mock<IJSRuntime> MakeThrowingMock(string exceptionType)
    {
        var js = new Mock<IJSRuntime>();
        Exception exception = exceptionType switch
        {
            "JSException" => new JSException("faro not loaded"),
            "JSDisconnectedException" => new JSDisconnectedException("disconnected"),
            "OperationCanceledException" => new OperationCanceledException(),
            "ObjectDisposedException" => new ObjectDisposedException("component"),
            "InvalidOperationException" => new InvalidOperationException("prerender"),
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType))
        };
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                It.IsAny<string>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(exception);
        return js;
    }

    // ── NotificationService integration ──────────────────────────────────────

    [Fact]
    public async Task PushErrorAsync_CalledByNotificationBridge_WhenErrorNotificationAdded()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        notifications.Add("pipeline dispatch failed", NotificationSeverity.Error);
        await bridge.FlushAsync();

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.Is<object?[]?>(args => args != null && ((string)args[0]!).Contains("pipeline dispatch failed"))),
            Times.Once);
    }

    [Fact]
    public async Task PushErrorAsync_NotCalled_WhenInfoNotificationAdded()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        notifications.Add("all good", NotificationSeverity.Info);
        await bridge.FlushAsync();

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.IsAny<object?[]?>()),
            Times.Never);
    }

    [Fact]
    public async Task PushLogAsync_Called_WhenInfoNotificationAdded()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        notifications.Add("job queued", NotificationSeverity.Info);
        await bridge.FlushAsync();

        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.Is<object?[]?>(args => args != null && ((string)args[0]!).Contains("job queued"))),
            Times.Once);
    }

    [Fact]
    public async Task FlushAsync_CalledTwiceWithNoNewEntries_IsIdempotent()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        notifications.Add("first", NotificationSeverity.Info);
        await bridge.FlushAsync(); // flush the first entry
        await bridge.FlushAsync(); // second flush — nothing new

        // pushLog called exactly once (for "first"), not twice
        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.IsAny<object?[]?>()),
            Times.Once);
    }

    [Fact]
    public async Task FlushAsync_SecondBatch_OnlyForwardsNewEntries()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        notifications.Add("batch-1", NotificationSeverity.Info);
        await bridge.FlushAsync(); // flush 1 entry

        notifications.Add("batch-2a", NotificationSeverity.Info);
        notifications.Add("batch-2b", NotificationSeverity.Info);
        await bridge.FlushAsync(); // flush 2 more

        // Total: 3 pushLog calls (not 5 or 1)
        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.IsAny<object?[]?>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task FlushAsync_AfterRingBufferCapReached_StillForwardsNewEntries()
    {
        // Regression test for the ring-buffer silent-drop bug:
        // NotificationService caps at 50 entries with eviction.
        // The old count-based tracking reached 50 and never advanced, silently dropping all
        // subsequent entries. The timestamp-based watermark must keep forwarding after cap.
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        // Fill to cap (MaxEntries = 50) and flush — establishes watermark
        for (int i = 0; i < 50; i++)
            notifications.Add($"filling-{i}", NotificationSeverity.Info);
        await bridge.FlushAsync();

        // Reset invocation count so we only count calls AFTER the cap
        js.ResetCalls();

        // Add one more entry — this evicts the oldest entry from the ring buffer
        notifications.Add("post-cap-entry", NotificationSeverity.Error);
        await bridge.FlushAsync();

        // Must forward the post-cap entry, not silently drop it
        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.Is<object?[]?>(args => args != null && ((string)args[0]!).Contains("post-cap-entry"))),
            Times.Once);
    }

    [Fact]
    public async Task FlushAsync_MultipleEntriesAfterCap_ForwardsAll()
    {
        var (sut, js) = Create();
        var notifications = new NotificationService();
        var bridge = new NotificationFaroBridge(notifications, sut);

        // Fill cap and flush
        for (int i = 0; i < 50; i++)
            notifications.Add($"fill-{i}", NotificationSeverity.Info);
        await bridge.FlushAsync();

        js.ResetCalls();

        // Add 3 more entries after cap
        notifications.Add("post-a", NotificationSeverity.Error);
        notifications.Add("post-b", NotificationSeverity.Error);
        notifications.Add("post-c", NotificationSeverity.Info);
        await bridge.FlushAsync();

        // 2 errors + 1 log
        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushError",
            It.IsAny<object?[]?>()),
            Times.Exactly(2));
        js.Verify(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
            "faroApi.pushLog",
            It.IsAny<object?[]?>()),
            Times.Once);
    }
}
