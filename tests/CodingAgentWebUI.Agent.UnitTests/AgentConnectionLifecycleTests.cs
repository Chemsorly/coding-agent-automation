using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentConnectionLifecycle"/> IAsyncDisposable implementation:
/// - Interface compliance
/// - Atomic disposal via Interlocked.Exchange
/// - Race-freedom between DisposeAsync and ShutdownAsync
/// - Idempotent disposal
/// </summary>
public class AgentConnectionLifecycleTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(AgentConnectionLifecycle))
            .Should().BeTrue("AgentConnectionLifecycle must implement IAsyncDisposable to prevent connection leaks");
    }

    [Fact]
    public void Instance_IsAssignableTo_IAsyncDisposable()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();
        lifecycle.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── DisposeAsync behavior ────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        var act = async () => await lifecycle.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent_DoesNotThrow()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        // First disposal
        await lifecycle.DisposeAsync();

        // Second disposal — must not throw
        var act = async () => await lifecycle.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // TODO: These two tests validate null-guard behavior of property getters but not actual resource
    // disposal. They would pass if DisposeAsync simply set _hubManager = null without calling
    // SafeDisposeAsync(manager). Add a test that verifies HubConnectionManager.DisposeAsync() is
    // actually invoked (e.g., via a mock or spy) to catch connection leak regressions.
    [Fact]
    public async Task DisposeAsync_NullsHubManager_IsConnectedReturnsFalse()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        lifecycle.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_NullsHubManager_ConnectionThrowsObjectDisposedException()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        var act = () => lifecycle.Connection;
        act.Should().Throw<ObjectDisposedException>();
    }

    // ── DisposeAsync + ShutdownAsync race freedom ────────────────────────

    [Fact]
    public async Task DisposeAsync_ThenShutdownAsync_DoesNotThrow()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        await lifecycle.DisposeAsync();

        // ShutdownAsync after disposal must not throw (NRE or ObjectDisposedException)
        var act = async () => await lifecycle.ShutdownAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ShutdownAsync_ThenDisposeAsync_DoesNotThrow()
    {
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        // ShutdownAsync first (will fail to deregister since not connected, but should not throw)
        await lifecycle.ShutdownAsync();

        // Then DisposeAsync — must not throw
        var act = async () => await lifecycle.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // TODO: This test validates exception suppression but not atomicity — it would pass even if
    // Interlocked.Exchange were replaced with a plain null assignment. The HubConnectionManager is
    // never started, so the race window is effectively empty. Consider starting the connection
    // or using a spy to verify exactly-once disposal.
    [Fact]
    public async Task ConcurrentDisposeAndShutdown_NoObjectDisposedException()
    {
        // Run multiple concurrent calls to verify no ObjectDisposedException
        var (_, _, lifecycle) = TestAgentWorkerServiceFactory.CreateWithComponents();

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(lifecycle.DisposeAsync().AsTask());
            tasks.Add(lifecycle.ShutdownAsync());
        }

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    // ── Structural verification ──────────────────────────────────────────

    [Fact]
    public void SourceCode_UsesInterlockedExchange_InDisposeAsync()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        sourceCode.Should().Contain("Interlocked.Exchange(ref _hubManager, null)",
            "DisposeAsync must use Interlocked.Exchange to atomically null the field");
    }

    [Fact]
    public void SourceCode_FieldIsNullable()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        sourceCode.Should().Contain("volatile HubConnectionManager? _hubManager",
            "The _hubManager field must be nullable to hold null after disposal");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
