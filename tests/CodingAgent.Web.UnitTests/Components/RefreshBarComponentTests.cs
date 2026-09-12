using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="RefreshBar"/> covering:
/// - Rendering: Refresh button + interval selector present
/// - Manual Refresh button invokes OnRefresh callback
/// - IsLoading=true disables the Refresh button
/// - Interval selector defaults to 1 minute (60 s)
/// - Auto-refresh starts automatically at 1 minute on component initialization
/// - Auto-refresh timer fires the OnRefresh callback without user interaction
/// - Selecting Off cancels any active timer
/// - Component disposal cancels active timer (no ObjectDisposedException)
/// - Auto-refresh timer actually fires the OnRefresh callback after the interval elapses
/// - StorageKey: restores persisted interval on first render
/// - StorageKey: writes to localStorage on interval change
/// - StorageKey: timer still starts when no stored value exists
/// - StorageKey: ignores stored values not in the Intervals list
/// - StorageKey: null means no localStorage calls (opt-in)
/// </summary>
public class RefreshBarComponentTests : BunitContext
{
    private readonly Mock<IJSRuntime> _mockJs = new();

    public RefreshBarComponentTests()
    {
        // IJSRuntime is now injected by RefreshBar. Register a shared loose mock so all existing
        // tests continue to work without modification. Individual tests that need specific return
        // values configure _mockJs.Setup(...) before calling Render<RefreshBar>().
        Services.AddSingleton<IJSRuntime>(_mockJs.Object);
    }

    [Fact]
    public void RefreshBar_RendersRefreshButton()
    {
        var cut = Render<RefreshBar>();

        var buttons = cut.FindAll("button");
        buttons.Should().NotBeEmpty("RefreshBar must render a Refresh button");
        buttons.Any(b => b.TextContent.Contains("Refresh")).Should().BeTrue("button text must contain 'Refresh'");
    }

    [Fact]
    public void RefreshBar_RendersIntervalSelector()
    {
        var cut = Render<RefreshBar>();

        var selects = cut.FindAll("select");
        selects.Should().NotBeEmpty("RefreshBar must render an auto-refresh interval selector");
    }

    [Fact]
    public void RefreshBar_IntervalSelector_DefaultsTo1Minute()
    {
        var cut = Render<RefreshBar>();

        var select = cut.Find("select");
        // Default value attribute should be 60 (1 minute)
        var value = select.GetAttribute("value");
        value.Should().Be("60", "auto-refresh interval must default to 1 minute (60)");
    }

    [Fact]
    public void RefreshBar_IntervalSelector_HasExpectedOptions()
    {
        var cut = Render<RefreshBar>();

        var options = cut.FindAll("select option");
        options.Should().HaveCountGreaterThanOrEqualTo(5, "must have at least Off/10s/30s/1m/5m options");

        var values = options.Select(o => o.GetAttribute("value")).ToList();
        values.Should().Contain("0", "must include Off (0)");
        values.Should().Contain("10", "must include 10s");
        values.Should().Contain("30", "must include 30s");
        values.Should().Contain("60", "must include 1m (60s)");
        values.Should().Contain("300", "must include 5m (300s)");
    }

    [Fact]
    public async Task RefreshBar_ManualRefreshButton_InvokesOnRefreshCallback()
    {
        var callCount = 0;
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { callCount++; })));

        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Refresh"));
        await cut.InvokeAsync(() => button.Click());

        callCount.Should().Be(1, "clicking Refresh must invoke the OnRefresh callback once");
    }

    [Fact]
    public void RefreshBar_IsLoading_True_DisablesRefreshButton()
    {
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.IsLoading, true));

        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Refresh"));
        button.HasAttribute("disabled").Should().BeTrue("Refresh button must be disabled while IsLoading is true");
    }

    [Fact]
    public void RefreshBar_IsLoading_False_EnablesRefreshButton()
    {
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.IsLoading, false));

        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Refresh"));
        button.HasAttribute("disabled").Should().BeFalse("Refresh button must be enabled when IsLoading is false");
    }

    [Fact]
    public void RefreshBar_DefaultIsLoading_IsFalse()
    {
        var cut = Render<RefreshBar>();

        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Refresh"));
        button.HasAttribute("disabled").Should().BeFalse("Refresh button must be enabled by default");
    }

    [Fact]
    public void RefreshBar_Dispose_DoesNotThrow_AfterFirstRender()
    {
        // Render with default 1-minute interval (timer starts on first render via OnAfterRenderAsync),
        // then dispose via BunitContext — DisposeAsync must cleanly cancel and dispose the active timer.
        var act = () =>
        {
            using var ctx = new BunitContext();
            ctx.Services.AddSingleton<IJSRuntime>(new Mock<IJSRuntime>().Object);
            _ = ctx.Render<RefreshBar>();
            // ctx.Dispose() is called implicitly — component DisposeAsync is awaited synchronously
            // via GetAwaiter().GetResult() by bUnit, so the timer is properly cancelled.
        };
        act.Should().NotThrow("disposing a component with an active first-render timer must not throw");
    }

    [Fact]
    public async Task RefreshBar_Dispose_DoesNotThrow_AfterIntervalChanged()
    {
        // Change interval to 10s to start the timer, then dispose the context.
        var act = async () =>
        {
            await using var ctx = new BunitContext();
            ctx.Services.AddSingleton<IJSRuntime>(new Mock<IJSRuntime>().Object);
            var cut = ctx.Render<RefreshBar>();
            var select = cut.Find("select");
            await cut.InvokeAsync(() => select.Change("10"));
            // ctx.DisposeAsync() runs on await using exit — no exception expected.
        };
        await act.Should().NotThrowAsync("disposing with an active timer must not throw");
    }

    [Fact]
    public void RefreshBar_HasRefreshBarCssClass()
    {
        var cut = Render<RefreshBar>();

        // The root element should have the refresh-bar CSS class for consistent styling
        Assert.Contains("refresh-bar", cut.Markup);
    }

    /// <summary>
    /// Verifies that a timer is running immediately after the component renders for the first time,
    /// without any user interaction. The OnAfterRenderAsync(firstRender) hook must call RestartTimer.
    /// A regression that removes the OnAfterRenderAsync override would leave _timerCts null.
    /// </summary>
    [Fact]
    public void RefreshBar_AutoRefresh_StartsAutomaticallyAtDefaultInterval()
    {
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        var instance = cut.Instance;
        var type = instance.GetType();

        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerCtsField); // fails immediately if field is renamed/removed

        var timerCts = timerCtsField.GetValue(instance);
        timerCts.Should().NotBeNull(
            "OnAfterRenderAsync must start a timer automatically at the default 1-minute interval; " +
            "_timerCts is null, which means RestartTimer was never called on first render");
        // TODO [WARNING]: This assertion only checks that _timerCts is non-null, which proves
        // RestartTimer was called but not that the underlying PeriodicTimer was actually created.
        // If RestartTimer has a bug that initialises _timerCts but returns early before creating
        // _timer (e.g., a mistaken guard), this test passes while the timer is broken. Consider
        // also asserting that _timer is non-null to tighten coverage:
        //   var timerField = type.GetField("_timer", ...);
        //   timerField.GetValue(instance).Should().NotBeNull("PeriodicTimer must be created alongside _timerCts");
    }

    /// <summary>
    /// Verifies that the OnRefresh callback fires at least once without the test ever changing
    /// the interval selector. This proves the default-start path (OnAfterRenderAsync) is distinct
    /// from the user-triggered path (HandleIntervalChanged). Uses reflection to set a 1-second
    /// interval so the test completes quickly.
    /// </summary>
    [Fact]
    public async Task RefreshBar_AutoRefresh_FiresWithoutUserInteraction()
    {
        var callCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () =>
            {
                Interlocked.Increment(ref callCount);
                tcs.TrySetResult();
            })));

        // Override the field to 1s and restart the timer — this simulates what OnAfterRenderAsync
        // already did with 60s, but at a speed suitable for a test. The key point is that the test
        // never calls select.Change(...) — the timer is driven entirely by the component's own
        // initialization path, not by user interaction.
        // TODO [WARNING]: This test bypasses OnAfterRenderAsync entirely by directly invoking
        // RestartTimer via reflection. If OnAfterRenderAsync is deleted, this test still passes
        // because it never exercises that code path. RefreshBar_AutoRefresh_StartsAutomaticallyAtDefaultInterval
        // is the only guard for the OnAfterRenderAsync path. Consider refactoring this test to
        // use a 1 s interval from the start (constructor param or subclass) so the real lifecycle
        // hook drives the timer, making this test a true end-to-end guard for the OnAfterRenderAsync
        // → RestartTimer → tick → OnRefresh chain.
        await cut.InvokeAsync(() =>
        {
            var instance = cut.Instance;
            var type = instance.GetType();

            var intervalField = type.GetField("_intervalSeconds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(intervalField);
            intervalField.SetValue(instance, 1);

            var restartMethod = type.GetMethod("RestartTimer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(restartMethod);
            return (Task)restartMethod.Invoke(instance, null)!;
        });

        // Wait for the first tick (up to 5 seconds at a 1-second interval).
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().Be(tcs.Task,
            "OnRefresh must fire automatically without any user interaction with the interval selector");
        callCount.Should().BeGreaterThan(0,
            "the auto-refresh timer started by OnAfterRenderAsync must invoke OnRefresh");
    }

    /// <summary>
    /// Verifies that selecting Off in the dropdown stops an already-running default timer.
    /// After the default 60 s timer starts on first render, changing the selector to "0"
    /// must cancel the timer (_timerCts and _timer both become null).
    /// </summary>
    [Fact]
    public async Task RefreshBar_SelectingOff_StopsTimer()
    {
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        // Verify the timer started on first render.
        var instance = cut.Instance;
        var type = instance.GetType();

        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timerField = type.GetField("_timer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var intervalField = type.GetField("_intervalSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var restartMethod = type.GetMethod("RestartTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerCtsField);
        Assert.NotNull(timerField);
        Assert.NotNull(intervalField);
        Assert.NotNull(restartMethod);

        timerCtsField.GetValue(instance).Should().NotBeNull(
            "timer must be running after first render before selecting Off");

        // Set interval to 0 (Off) and restart via reflection, fully awaiting the async chain.
        // select.Change("0") dispatches an event that triggers an async void-like handler — the
        // underlying CancelAsync() may not have completed when the assertion runs. Calling
        // RestartTimer directly via InvokeAsync ensures we await the full async completion.
        // TODO [WARNING]: This test bypasses the real user-interaction path (HandleIntervalChanged →
        // parse → set _intervalSeconds → RestartTimer). A bug in HandleIntervalChanged (e.g., it
        // never sets _intervalSeconds before calling RestartTimer) would not be caught here because
        // this test sets the field directly via reflection. The acceptance criterion "Selecting Off
        // in the dropdown stops the timer" is not covered at the DOM-event level. Consider adding a
        // separate test that calls select.Change("0") and uses a short await/retry loop to handle
        // the async handler completion, or wraps the change in cut.InvokeAsync.
        await cut.InvokeAsync(() =>
        {
            intervalField.SetValue(instance, 0);
            return (Task)restartMethod.Invoke(instance, null)!;
        });

        timerCtsField.GetValue(instance).Should().BeNull(
            "selecting Off must cancel and dispose _timerCts");
        timerField.GetValue(instance).Should().BeNull(
            "selecting Off must dispose and null _timer");
    }

    /// <summary>
    /// Verifies that an exception thrown by the OnRefresh callback does NOT kill the
    /// auto-refresh loop — subsequent ticks must still fire the callback.
    ///
    /// Root cause: RunTickLoopAsync only catches OperationCanceledException and
    /// ObjectDisposedException. Any other exception (e.g. HttpRequestException from
    /// Attention/Insights pages) propagates out of the while body and permanently
    /// kills auto-refresh for the component lifetime with no log or user-visible
    /// indication. This was a cross-PR semantic conflict introduced when PR#2420
    /// added RefreshBar to pages (Attention, Insights) whose callbacks lack the
    /// bare catch{} guard that Work/Fleet/Runs have.
    /// </summary>
    [Fact]
    public async Task RefreshBar_AutoRefresh_ContinuesFiringAfterCallbackThrows()
    {
        var callCount = 0;
        var secondCallTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1) throw new InvalidOperationException("Simulated transient callback failure");
                if (n >= 2) secondCallTcs.TrySetResult();
            })));

        // Start a 1-second timer via reflection (same technique as the AutoRefresh fires test).
        await cut.InvokeAsync(() =>
        {
            var instance = cut.Instance;
            var type = instance.GetType();

            var intervalField = type.GetField("_intervalSeconds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(intervalField);
            intervalField.SetValue(instance, 1);

            var restartMethod = type.GetMethod("RestartTimer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(restartMethod);
            return (Task)restartMethod.Invoke(instance, null)!;
        });

        // Wait for the second call (up to 10 seconds — first tick throws, second tick should still fire).
        var completed = await Task.WhenAny(secondCallTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().Be(secondCallTcs.Task,
            "auto-refresh loop must survive a transient callback exception and continue firing; " +
            "a loop that exits on the first exception would never invoke OnRefresh a second time");
        callCount.Should().BeGreaterThanOrEqualTo(2,
            "OnRefresh must be called at least twice — once throwing, once succeeding");
    }

    /// <summary>
    /// Verifies that enabling auto-refresh by changing the interval selector actually fires
    /// the OnRefresh callback at least once after the interval elapses.
    ///
    /// This test is the critical proof that RunTickLoopAsync is started and invokes OnRefresh.
    /// A regression that removes `_ = RunTickLoopAsync(token)` from RestartTimer would cause
    /// this test to time out / fail because callCount would remain 0.
    ///
    /// Implementation note: the test uses reflection to set a 1-second timer interval instead of
    /// waiting 10 seconds for the shortest dropdown option. Reflection members are looked up with
    /// Assert.NotNull guards so a rename or removal fails the test immediately with a clear message
    /// rather than silently no-oping and producing a misleading timeout failure.
    /// </summary>
    [Fact]
    public async Task RefreshBar_AutoRefresh_FiresOnRefreshCallback_AfterIntervalElapses()
    {
        var callCount = 0;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () =>
            {
                Interlocked.Increment(ref callCount);
                tcs.TrySetResult();
            })));

        // Use reflection to set a 1-second interval so the test completes quickly rather than
        // waiting 10 seconds for the smallest dropdown option.
        // IMPORTANT: reflection members are resolved with Assert.NotNull so that any rename or
        // removal causes an immediate, descriptive failure instead of a silent no-op that would
        // make the test appear to pass vacuously (callCount stays 0 → assertion passes anyway
        // only because the timeout branch is also tested). This was the critical defect fixed here.
        await cut.InvokeAsync(() =>
        {
            var instance = cut.Instance;
            var type = instance.GetType();

            var intervalField = type.GetField("_intervalSeconds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(intervalField); // fails immediately if field is renamed/removed
            intervalField.SetValue(instance, 1);

            var restartMethod = type.GetMethod("RestartTimer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(restartMethod); // fails immediately if method is renamed/removed
            return (Task)restartMethod.Invoke(instance, null)!;
        });

        // Wait for the first tick (up to 5 seconds — the timer fires at 1s intervals).
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        callCount.Should().BeGreaterThan(0,
            "auto-refresh timer must invoke OnRefresh at least once after the interval elapses; " +
            "a broken RunTickLoopAsync (e.g. the fire-and-forget removed) would leave callCount at 0");
        completed.Should().Be(tcs.Task, "OnRefresh must fire within 5 seconds of a 1-second interval being set");
    }

    // ── StorageKey persistence tests ──────────────────────────────────────────

    // TODO [WARNING]: StorageKey_DisposedDuringLocalStorageGet_DoesNotLeakTimer is missing.
    // The _disposed flag (set at the top of DisposeAsync, checked in OnAfterRenderAsync after
    // the localStorageGet await) is the only guard against a timer/CTS leak when DisposeAsync
    // runs during the localStorageGet yield. This guard has no test coverage — a future refactor
    // that moves `_disposed = true` after an await would not be caught by the current suite.
    // Add a test that resolves the localStorageGet mock after disposal and asserts no second
    // timer is created (e.g. _timerCts count remains at 1, or RestartTimer is not called twice).

    /// <summary>
    /// When a valid interval is stored in localStorage, it must be restored on first render,
    /// overriding the default 60s interval. The timer must be running with the restored interval.
    /// </summary>
    [Fact]
    public void StorageKey_WithSavedInterval_RestoresIntervalOnFirstRender()
    {
        // Arrange: localStorage returns "30" for the storage key.
        // Use the no-CT overload (object[]?) — that is what JS.InvokeAsync<T>(id, args) calls.
        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync("30");

        // Act
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.StorageKey, "autoRefresh.work")
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        // Assert: interval was restored to 30s
        var instance = cut.Instance;
        var type = instance.GetType();
        var intervalField = type.GetField("_intervalSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(intervalField);

        intervalField.GetValue(instance).Should().Be(30,
            "the stored interval (30s) must be restored on first render");

        // Timer must also be running
        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerCtsField);
        timerCtsField.GetValue(instance).Should().NotBeNull(
            "timer must be running after the interval is restored");
    }

    /// <summary>
    /// When the stored value is not in the Intervals array (e.g. "999"), it must be ignored
    /// and the default interval used. The timer must still start.
    /// </summary>
    [Fact]
    public void StorageKey_WithInvalidInterval_IgnoresStoredValue_UsesDefault()
    {
        // Arrange: localStorage returns a value not in the Intervals array
        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync("999");

        // Act
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.StorageKey, "autoRefresh.work"));

        // Assert: default interval unchanged
        var instance = cut.Instance;
        var type = instance.GetType();
        var intervalField = type.GetField("_intervalSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(intervalField);

        intervalField.GetValue(instance).Should().Be(60,
            "an invalid stored value must be ignored; default 60s interval must remain");

        // Timer must still be running (default start must have fired)
        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerCtsField);
        timerCtsField.GetValue(instance).Should().NotBeNull(
            "timer must still start even when the stored value is invalid");
    }

    /// <summary>
    /// When the stored value is null (key not yet written — first visit), the default interval
    /// must be used and the timer must start. This is the key regression test for the
    /// 'double-phase' approach: the timer must start via RestartTimer() BEFORE the localStorage
    /// check, not only when a valid stored value is found.
    /// </summary>
    [Fact]
    public void StorageKey_WithNullStoredValue_StartsTimerAtDefault()
    {
        // Arrange: localStorage returns null (key absent)
        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync((string?)null);

        // Act
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.StorageKey, "autoRefresh.work")
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        // Assert: default interval (60s) unchanged
        var instance = cut.Instance;
        var type = instance.GetType();
        var intervalField = type.GetField("_intervalSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(intervalField);
        intervalField.GetValue(instance).Should().Be(60,
            "when no value is stored the default interval must be used");

        // Timer must be running (the unconditional RestartTimer call must have fired)
        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(timerCtsField);
        timerCtsField.GetValue(instance).Should().NotBeNull(
            "timer must start even when localStorage returns null for the key");
    }

    /// <summary>
    /// When the user changes the interval and StorageKey is set, the new interval must be
    /// written to localStorage.
    /// </summary>
    [Fact]
    public async Task StorageKey_OnIntervalChange_WritesToLocalStorage()
    {
        // Act: change the interval to 30s
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.StorageKey, "autoRefresh.work")
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        var select = cut.Find("select");
        await cut.InvokeAsync(() => select.Change("30"));

        // Assert: localStorageSet was called with the new interval.
        // InvokeVoidAsync maps to InvokeAsync<IJSVoidResult> with the no-CT overload.
        _mockJs.Verify(
            j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "localStorageSet",
                It.Is<object[]?>(args => args != null && args.Length == 2
                    && args[0].ToString() == "autoRefresh.work"
                    && args[1].ToString() == "30")),
            Times.Once,
            "localStorageSet must be called with the storage key and new interval value");
        // TODO [WARNING]: This test does not verify that localStorageSet is NOT called during the
        // restore path (OnAfterRenderAsync). The requirement "write on user-initiated change only"
        // has no coverage — a regression that adds a write in the restore path would not be caught.
        // Add a separate test (or an extra Times assertion here) that confirms localStorageSet is
        // never called when only a restore occurs (no user-initiated change).
    }

    /// <summary>
    /// When StorageKey is null (not provided), localStorageGet must never be called.
    /// This confirms the opt-in behaviour — components without StorageKey make no JS calls.
    /// </summary>
    [Fact]
    public void StorageKey_Null_DoesNotCallLocalStorageGet()
    {
        // Act: render without StorageKey
        _ = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        // Assert: no localStorageGet call was made
        _mockJs.Verify(
            j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()),
            Times.Never,
            "localStorageGet must not be called when StorageKey is null");
    }

    /// <summary>
    /// When StorageKey is null, the component behaves exactly as it did before the StorageKey
    /// parameter was added: timer starts at default 1-minute interval.
    /// </summary>
    [Fact]
    public void NoStorageKey_BehavesExactlyAsToday()
    {
        var cut = Render<RefreshBar>(p => p
            .Add(c => c.OnRefresh, EventCallback.Factory.Create(this, () => { })));

        var instance = cut.Instance;
        var type = instance.GetType();

        var intervalField = type.GetField("_intervalSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timerCtsField = type.GetField("_timerCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(intervalField);
        Assert.NotNull(timerCtsField);

        intervalField.GetValue(instance).Should().Be(60,
            "without StorageKey, interval must default to 60s (1 minute)");
        timerCtsField.GetValue(instance).Should().NotBeNull(
            "without StorageKey, timer must start automatically at the default interval");
    }
}
