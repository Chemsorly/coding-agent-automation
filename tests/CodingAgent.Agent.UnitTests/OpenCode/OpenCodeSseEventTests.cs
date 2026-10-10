using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// The provider against OpenCode 1.18.35's real event stream (GET /event frames
/// <c>{"type": ..., "properties": {...}}</c>) and prompt responses.
/// </summary>
public class OpenCodeSseEventTests
{
    private const string Session = "ses_main";

    private static string Frame(string type, object properties) =>
        $"data: {JsonSerializer.Serialize(new { id = "evt", type, properties })}\n\n";

    private static string AssistantMessage(string messageId, string session = Session) =>
        Frame("message.updated", new { sessionID = session, info = new { id = messageId, sessionID = session, role = "assistant" } });

    private static string TextPart(string partId, string messageId, string text, bool complete = true, string session = Session) =>
        Frame("message.part.updated", new
        {
            sessionID = session,
            time = 1,
            part = new
            {
                id = partId, sessionID = session, messageID = messageId, type = "text", text,
                time = complete ? new { start = 1L, end = (long?)2 } : new { start = 1L, end = (long?)null }
            }
        });

    private static string ToolPart(string callId, string tool, object state) =>
        Frame("message.part.updated", new
        {
            sessionID = Session,
            time = 1,
            part = new { id = "prt_" + callId, sessionID = Session, messageID = "msg_a", type = "tool", callID = callId, tool, state }
        });

    private static async Task<(List<string> Lines, SseStreamMockHandler Handler)> ProcessAsync(string sse)
    {
        var handler = new SseStreamMockHandler(sse);
        var provider = new OpenCodeAgentProvider(new SseStreamClientFactory(handler), new Mock<ILogger>().Object);
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.ConnectAndProcessSseAsync(Session, lines.Add, cts.Token, "/ws");
        return (lines, handler);
    }

    // ── Output ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CompletedAssistantTextPart_IsEmittedOnce_ButNotTheUsersPromptOrAnUnfinishedPart()
    {
        var sse = AssistantMessage("msg_a")
            + TextPart("prt_user", "msg_user", "the user's prompt")       // no assistant message.updated for it
            + TextPart("prt_1", "msg_a", "first line\nsecond line", complete: false)
            + TextPart("prt_1", "msg_a", "first line\nsecond line")
            + TextPart("prt_1", "msg_a", "first line\nsecond line");      // repeated update

        var (lines, _) = await ProcessAsync(sse);

        lines.Should().Equal("first line", "second line");
    }

    [Fact]
    public async Task EventsOfOtherSessions_AreIgnored()
    {
        var (lines, _) = await ProcessAsync(AssistantMessage("msg_x", "ses_other") + TextPart("prt_x", "msg_x", "not ours", session: "ses_other"));

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolPart_IsAnnouncedOncePerCall_AndItsErrorShown()
    {
        var sse = ToolPart("c1", "bash", new { status = "pending", input = new { } })
            + ToolPart("c1", "bash", new { status = "running", input = new { }, title = "Run tests", time = new { start = 1 } })
            + ToolPart("c1", "bash", new { status = "completed", input = new { }, title = "Run tests", output = "ok", metadata = new { }, time = new { start = 1, end = 2 } })
            + ToolPart("c2", "edit", new { status = "error", input = new { }, error = "file not found", time = new { start = 1, end = 2 } });

        var (lines, _) = await ProcessAsync(sse);

        lines.Should().Equal("▶ bash: Run tests", "✖ edit: file not found");
    }

    // ── Requests the agent cannot wait for ──────────────────────────────

    [Fact]
    public async Task PermissionAsked_IsAnsweredAlways_EvenFromASubagentsChildSession()
    {
        var sse = Frame("permission.asked", new
        {
            id = "per_1", sessionID = "ses_child", permission = "read", patterns = new[] { ".env" },
            metadata = new { }, always = new[] { "*" }
        });

        var (_, handler) = await ProcessAsync(sse);

        var reply = handler.Requests.Should().ContainSingle(r => r.Path == "/permission/per_1/reply").Which;
        reply.Method.Should().Be(HttpMethod.Post);
        JsonDocument.Parse(reply.Body!).RootElement.GetProperty("reply").GetString().Should().Be("always");
        reply.Headers["x-opencode-directory"].Should().Equal("/ws");
    }

    [Fact]
    public async Task QuestionAsked_IsRejected_SinceNoOneCanAnswerIt()
    {
        var (_, handler) = await ProcessAsync(Frame("question.asked", new { id = "que_1", sessionID = Session, questions = Array.Empty<object>() }));

        handler.Requests.Should().ContainSingle(r => r.Path == "/question/que_1/reject" && r.Method == HttpMethod.Post);
    }

    // ── Full calls ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TextTheStreamShowed_IsNotShownAgainFromTheResponse()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, Session);
        ctx.Handler.ForUrlPattern("/event", HttpStatusCode.OK,
            AssistantMessage("msg_a") + TextPart("prt_1", "msg_a", "streamed"), "text/event-stream");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Info = new SendMessageInfo { Id = "msg_a" },
            Parts =
            [
                new MessagePart { Id = "prt_1", Type = "text", Text = "streamed" },
                new MessagePart { Id = "prt_2", Type = "text", Text = "final" }
            ]
        });
        var lines = new List<string>();

        var result = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None, lines.Add);

        result.ExitCode.Should().Be(ExitCodes.Success);
        lines.Should().BeEquivalentTo(["streamed", "final"]);
        result.OutputLines.Should().Contain(["streamed", "final"]);
    }

    [Theory]
    [InlineData("""{"name":"APIError","data":{"message":"rate limited","statusCode":429,"isRetryable":true}}""", AgentErrorCategory.ProviderRateLimit)]
    [InlineData("""{"name":"APIError","data":{"message":"overloaded","statusCode":529,"isRetryable":true}}""", AgentErrorCategory.ProviderOverload)]
    [InlineData("""{"name":"ProviderAuthError","data":{"providerID":"anthropic","message":"invalid x-api-key"}}""", AgentErrorCategory.PermanentAuthFailure)]
    [InlineData("""{"name":"UnknownError","data":{"message":"boom"}}""", AgentErrorCategory.None)]
    public async Task ExecuteAsync_TurnThatFailedWithHttp200_IsAFailure_Classified(string error, AgentErrorCategory expected)
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, Session);
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.OK,
            $$"""{"info":{"id":"msg_a","role":"assistant","error":{{error}}},"parts":[]}""");

        var result = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.ErrorCategory.Should().Be(expected);
        result.OutputLines.Should().ContainMatch("✖ OpenCode *");
    }

    [Theory]
    [InlineData("anthropic/claude-sonnet-4-5", "anthropic", "claude-sonnet-4-5")]
    [InlineData("openrouter/x-ai/grok-4", "openrouter", "x-ai/grok-4")]
    public async Task ExecuteAsync_ConfiguredModel_IsSentAsAModelRef(string model, string providerId, string modelId)
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model);
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, Session);
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse());

        await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

        var body = JsonDocument.Parse(ctx.Handler.Requests.Single(r => r.Path.EndsWith("/message")).Body!).RootElement;
        body.GetProperty("model").GetProperty("providerID").GetString().Should().Be(providerId);
        body.GetProperty("model").GetProperty("modelID").GetString().Should().Be(modelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("no-provider-prefix")]
    public void ModelRef_WithoutAProvider_LeavesTheModelToTheServer(string? model)
    {
        OpenCodeModelRef.Parse(model).Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_IsolatedCallAfterTheMainSession_DoesNotBecomeWhatUseResumeContinues()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        // Patterns, not the FIFO queue: token-usage GETs between calls would consume queued responses.
        ctx.Handler.ForUrlPattern("^/session$", new CreateSessionResponse { Id = "ses_analysis" });
        ctx.Handler.ForUrlPattern("^/session$", new CreateSessionResponse { Id = "ses_reviewer" });
        for (var i = 0; i < 3; i++)
            ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse());

        await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("analysis", useResume: true), CancellationToken.None);
        await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("review", useResume: false), CancellationToken.None);
        await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("codegen", useResume: true), CancellationToken.None);

        ctx.Handler.Requests.Where(r => r.Path.EndsWith("/message")).Select(r => r.Path)
            .Should().Equal("/session/ses_analysis/message", "/session/ses_reviewer/message", "/session/ses_analysis/message");
    }

    [Fact]
    public async Task ParseAndEmitResponseAsync_MalformedJson_ReturnsParseError()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-json-err");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.OK, "not-valid-json");

        var result = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("prompt"), CancellationToken.None);

        result.ExitCode.Should().NotBe(0, "malformed JSON must produce a failure result");
        result.OutputLines.Should().ContainMatch("*JSON parse error*");
    }
}

/// <summary>
/// A mock HTTP handler that returns an SSE stream for GET /event requests
/// and records all other requests (e.g., permission replies).
/// </summary>
internal sealed class SseStreamMockHandler : HttpMessageHandler
{
    private readonly string _sseContent;
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>All requests sent through this handler, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public SseStreamMockHandler(string sseContent)
    {
        _sseContent = sseContent;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList() as IReadOnlyList<string>)));

        var path = request.RequestUri?.PathAndQuery ?? string.Empty;
        if (request.Method == HttpMethod.Get && path.Contains("/event"))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(_sseContent)))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>A simple IHttpClientFactory that creates HttpClients backed by a <see cref="SseStreamMockHandler"/>.</summary>
internal sealed class SseStreamClientFactory : IHttpClientFactory
{
    private readonly SseStreamMockHandler _handler;

    public SseStreamClientFactory(SseStreamMockHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(AgentDefaults.OpenCodeBaseUrl)
        };
    }
}
