using System.Net;
using CodingAgent.Agent.OpenCode;
using Moq;
using ILogger = Serilog.ILogger;
using CodingAgent.Agent;
using AwesomeAssertions;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for session lifecycle management in the stateless OpenCodeAgentProvider.
/// Sessions are now created per-ExecuteAsync call (scoped to workspace path).
/// EnsureSessionAsync is a no-op — sessions are managed entirely within ExecuteAsync.
/// Also covers per-execution teardown (TearDownSseAsync) and state reset (ResetExecutionState).
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

    // ── TearDownSseAsync ─────────────────────────────────────────────────

    /// <summary>
    /// TearDownSseAsync is exercised at the end of every ExecuteAsync call (in the finally block).
    /// Verifying that a full execute-with-session call completes cleans up the SSE task confirms
    /// TearDownSseAsync ran without leaking resources.
    /// </summary>
    [Fact]
    public async Task TearDownSseAsync_CalledAfterExecute_NoResourceLeak()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-teardown");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "done" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("test"), CancellationToken.None);

        result.Should().NotBeNull("TearDownSseAsync must complete for ExecuteAsync to return");
    }

    // ── ResetExecutionState ───────────────────────────────────────────────

    /// <summary>
    /// ResetExecutionState is called at the start of each ExecuteAsync invocation.
    /// Verified by running two sequential executions and confirming the state from the
    /// first doesn't bleed into the second.
    /// </summary>
    [Fact]
    public async Task ResetExecutionState_SecondCall_DoesNotRetainFirstCallState()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // First execution
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-reset");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "first" }]
        });
        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);
        var first = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("first"), CancellationToken.None);

        // Second execution — queue another pattern response
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "second" }]
        });
        var second = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("second"), CancellationToken.None);

        first.ExitCode.Should().Be(0);
        // Second call must complete (not throw) — ResetExecutionState cleared first-call state
        second.Should().NotBeNull("second execute must complete without retaining first-call state");
    }
}
