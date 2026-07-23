using System.Text.RegularExpressions;
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

    [Fact]
    public void SourceCode_UsesInterlockedCompareExchange_InReconnection()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        sourceCode.Should().Contain("Interlocked.CompareExchange(ref _hubManager, newManager, oldManager)",
            "HandleTerminalClosedAsync must use Interlocked.CompareExchange to atomically swap the hub manager");
    }

    [Fact]
    public void SourceCode_NoDirectHubManagerAssignment_OutsideConstructor()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        // Find all bare _hubManager = assignments (not inside Interlocked calls)
        var matches = Regex.Matches(sourceCode, @"(?<!Interlocked\.\w+\(ref\s)_hubManager\s*=\s*");

        // Extract the constructor body to identify which matches are in the constructor
        var constructorStart = sourceCode.IndexOf("public AgentConnectionLifecycle(", StringComparison.Ordinal);
        var constructorBodyStart = sourceCode.IndexOf('{', constructorStart);

        // Find the matching closing brace for the constructor
        var braceCount = 0;
        var constructorEnd = -1;
        for (var i = constructorBodyStart; i < sourceCode.Length; i++)
        {
            if (sourceCode[i] == '{') braceCount++;
            else if (sourceCode[i] == '}') braceCount--;
            if (braceCount == 0) { constructorEnd = i; break; }
        }

        // All bare _hubManager = assignments must be within the constructor
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var isInConstructor = match.Index > constructorBodyStart && match.Index < constructorEnd;
            isInConstructor.Should().BeTrue(
                $"_hubManager assignment at position {match.Index} must be inside the constructor or use Interlocked. " +
                $"Found: ...{sourceCode.Substring(Math.Max(0, match.Index - 20), Math.Min(60, sourceCode.Length - match.Index))}...");
        }
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
