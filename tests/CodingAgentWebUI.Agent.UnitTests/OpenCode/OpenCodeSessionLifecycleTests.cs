using System.Net;
using CodingAgentWebUI.Agent.OpenCode;
using Moq;
using ILogger = Serilog.ILogger;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for session lifecycle management in the stateless OpenCodeAgentProvider.
/// Sessions are now created per-ExecuteAsync call (scoped to workspace path).
/// EnsureSessionAsync is a no-op — sessions are managed entirely within ExecuteAsync.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "8")]
public class OpenCodeSessionLifecycleTests
{
    private const string WorkspacePath = "/tmp/test-workspace";

    /// <summary>
    /// EnsureSessionAsync is a no-op in stateless design — no HTTP calls, no session stored.
    /// </summary>
    [Fact]
    public async Task EnsureSessionAsync_IsNoOp_MakesNoHttpCalls()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // No HTTP calls should be made
        Assert.Empty(ctx.Handler.Requests);

        // No session stored
        var sessionId = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Null(sessionId);
    }

    /// <summary>
    /// GetLatestSessionIdAsync returns null when no ExecuteAsync has been called.
    /// </summary>
    [Fact]
    public async Task GetLatestSessionId_NoExecution_ReturnsNull()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        var sessionId = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);

        Assert.Null(sessionId);
    }

    /// <summary>
    /// DisposeAsync clears last known session ID without HTTP calls.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ClearsState_NoHttpCalls()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        await ctx.Provider.DisposeAsync();

        var sessionId = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Null(sessionId);
        Assert.Empty(ctx.Handler.Requests);
    }
}
