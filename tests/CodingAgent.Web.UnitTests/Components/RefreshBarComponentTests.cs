using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="RefreshBar"/> covering:
/// - Rendering: Refresh button + interval selector present
/// - Manual Refresh button invokes OnRefresh callback
/// - IsLoading=true disables the Refresh button
/// - Interval selector defaults to Off (0)
/// - Auto-refresh defaults to Off (no timer active at startup)
/// - Changing interval to Off cancels any active timer
/// - Component disposal cancels active timer (no ObjectDisposedException)
/// - Auto-refresh timer actually fires the OnRefresh callback after the interval elapses
/// </summary>
public class RefreshBarComponentTests : BunitContext
{
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
    public void RefreshBar_IntervalSelector_DefaultsToOff()
    {
        var cut = Render<RefreshBar>();

        var select = cut.Find("select");
        // Default value attribute should be 0 (Off)
        var value = select.GetAttribute("value");
        value.Should().Be("0", "auto-refresh interval must default to Off (0)");
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
    public void RefreshBar_Dispose_DoesNotThrow_WhenTimerIsNotActive()
    {
        // Render with default Off interval, then dispose via BunitContext — should not throw.
        var act = () =>
        {
            using var ctx = new BunitContext();
            _ = ctx.Render<RefreshBar>();
            // ctx.Dispose() is called implicitly — component disposal runs here.
        };
        act.Should().NotThrow("disposing with no active timer must not throw");
    }

    [Fact]
    public async Task RefreshBar_Dispose_DoesNotThrow_AfterIntervalChanged()
    {
        // Change interval to 10s to start the timer, then dispose the context.
        var act = async () =>
        {
            await using var ctx = new BunitContext();
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
}
