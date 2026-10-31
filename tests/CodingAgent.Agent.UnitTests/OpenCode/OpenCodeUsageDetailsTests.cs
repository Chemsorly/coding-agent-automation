using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests verifying that <see cref="OpenCodeAgentProvider.ExecuteAsync"/> populates
/// <see cref="CodingAgent.Pipeline.Models.AgentResult.UsageDetails"/> with the number of
/// assistant turns observed on the SSE stream.
/// </summary>
public class OpenCodeUsageDetailsTests
{
    private const string CallSession = "ses_turns";

    // ── SSE frame helpers (copied from OpenCodeSseEventTests — private helpers cannot be shared) ──

    private static string Frame(string type, object properties) =>
        $"data: {JsonSerializer.Serialize(new { id = "evt", type, properties })}\n\n";

    private static string AssistantMessage(string messageId, string session = CallSession) =>
        Frame("message.updated", new { sessionID = session, info = new { id = messageId, sessionID = session, role = "assistant" } });

    // ── Setup helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Queues the minimum response set for a successful <c>ExecuteAsync</c> call:
    /// session creation (FIFO), SSE stream, and message response (URL patterns).
    /// Token-delta and MCP status calls are best-effort and fail silently when not queued.
    /// </summary>
    private static void SetupRun(MockOpenCodeHandler handler, string sessionId, string sseContent)
    {
        handler.EnqueueJsonResponse(new CreateSessionResponse { Id = sessionId });
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseContent, "text/event-stream");
        handler.ForUrlPattern("/session/.+/message", new SendMessageResponse { Parts = [] });
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReportsAssistantMessageCountAsTurns()
    {
        // Two assistant message.updated events for the call's session, one for another session.
        // The cross-session event must be filtered out by the sessionId guard in ProcessSseEventAsync.
        var sseContent =
            AssistantMessage("msg_a")                           // call's session — counted
            + AssistantMessage("msg_b")                        // call's session — counted
            + AssistantMessage("msg_c", "ses_other");          // different session — ignored

        var ctx = OpenCodeTestHelpers.CreateTestContext();
        SetupRun(ctx.Handler, CallSession, sseContent);

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("test prompt"), CancellationToken.None);

        result.UsageDetails.Should().NotBeNull();
        result.UsageDetails!.Turns.Should().Be(2);
        result.UsageDetails.BillingMode.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateAssistantMessageId_CountsMessageOnce()
    {
        // Real OpenCode streams fire multiple message.updated events with the same messageId
        // as a conversation turn is built up. AssistantMessageIds uses TryAdd (deduplication)
        // so the same ID must be counted only once regardless of how many events arrive.
        var sseContent =
            AssistantMessage("msg_a")   // first event for msg_a — counted
            + AssistantMessage("msg_a") // duplicate id — must not increase the count
            + AssistantMessage("msg_a"); // another duplicate

        var ctx = OpenCodeTestHelpers.CreateTestContext();
        SetupRun(ctx.Handler, CallSession, sseContent);

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("test prompt"), CancellationToken.None);

        result.UsageDetails.Should().NotBeNull();
        result.UsageDetails!.Turns.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_NoAssistantMessages_LeavesUsageDetailsNull()
    {
        // SSE stream with no message.updated events for the call's session.
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        SetupRun(ctx.Handler, CallSession, sseContent: string.Empty);

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("test prompt"), CancellationToken.None);

        result.UsageDetails.Should().BeNull();
    }
}
