using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests that parallel ExecuteAsync calls with UseResume=false each get their own
/// isolated session. Reproduces the race condition where shared _currentSessionId
/// causes multiple parallel calls to accidentally use the same session.
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "parallel-isolation")]
public class OpenCodeParallelSessionIsolationTests
{
    /// <summary>
    /// When 5 concurrent ExecuteAsync calls are made with UseResume=false,
    /// each must create and use its own session. No two calls should send
    /// messages to the same session ID.
    /// </summary>
    [Fact]
    public async Task ParallelExecute_UseResumeFalse_EachCallGetsUniqueSession()
    {
        // Arrange
        const int parallelCount = 5;
        var handler = new ConcurrentMockOpenCodeHandler();
        var factory = new ConcurrentMockClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var requests = Enumerable.Range(0, parallelCount)
            .Select(i => new AgentRequest
            {
                Prompt = $"review prompt {i}",
                WorkspacePath = Path.GetTempPath(),
                UseResume = false,
                Timeout = TimeSpan.FromSeconds(30)
            })
            .ToList();

        // Act — launch all calls concurrently
        var tasks = requests.Select(r => provider.ExecuteAsync(r, CancellationToken.None)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert — all calls should succeed
        Assert.All(results, r => Assert.Equal(ExitCodes.Success, r.ExitCode));

        // Assert — exactly N unique sessions created
        Assert.Equal(parallelCount, handler.CreatedSessionIds.Count);

        // Assert — each session received exactly 1 message (no sharing)
        var messageSessionIds = handler.MessageSessionIds.ToList();
        Assert.Equal(parallelCount, messageSessionIds.Count);

        var uniqueMessageTargets = messageSessionIds.Distinct().ToList();
        Assert.Equal(parallelCount, uniqueMessageTargets.Count);

        // Assert — every created session received a message
        foreach (var sessionId in handler.CreatedSessionIds)
        {
            Assert.Contains(sessionId, uniqueMessageTargets);
        }
    }

    /// <summary>
    /// When parallel calls race on session creation, _currentSessionId must not
    /// leak one call's session to another. Each call's message must target the
    /// session that call itself created.
    /// </summary>
    [Fact]
    public async Task ParallelExecute_SessionIdNotLeakedBetweenCalls()
    {
        // Arrange — use delays to maximize race window
        const int parallelCount = 3;
        var handler = new ConcurrentMockOpenCodeHandler(sessionCreationDelay: TimeSpan.FromMilliseconds(50));
        var factory = new ConcurrentMockClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var requests = Enumerable.Range(0, parallelCount)
            .Select(i => new AgentRequest
            {
                Prompt = $"prompt {i}",
                WorkspacePath = Path.GetTempPath(),
                UseResume = false,
                Timeout = TimeSpan.FromSeconds(30)
            })
            .ToList();

        // Act
        var tasks = requests.Select(r => provider.ExecuteAsync(r, CancellationToken.None)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert — no session received more than 1 message
        var grouped = handler.MessageSessionIds.GroupBy(id => id).ToList();
        Assert.All(grouped, g => Assert.Single(g));
    }

    /// <summary>
    /// Thread-safe mock handler that assigns unique session IDs atomically and
    /// tracks which sessions receive messages.
    /// </summary>
    private sealed class ConcurrentMockOpenCodeHandler : HttpMessageHandler
    {
        private int _sessionCounter;
        private readonly TimeSpan _sessionCreationDelay;

        /// <summary>All session IDs created via POST /session.</summary>
        public ConcurrentBag<string> CreatedSessionIds { get; } = [];

        /// <summary>Session IDs that received a POST /session/{id}/message.</summary>
        public ConcurrentBag<string> MessageSessionIds { get; } = [];

        public ConcurrentMockOpenCodeHandler(TimeSpan? sessionCreationDelay = null)
        {
            _sessionCreationDelay = sessionCreationDelay ?? TimeSpan.Zero;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // POST /session → create new session with unique ID
            if (request.Method == HttpMethod.Post && path == "/session")
            {
                if (_sessionCreationDelay > TimeSpan.Zero)
                    await Task.Delay(_sessionCreationDelay, cancellationToken);

                var id = $"session-{Interlocked.Increment(ref _sessionCounter):D3}";
                CreatedSessionIds.Add(id);

                var body = JsonSerializer.Serialize(
                    new CreateSessionResponse { Id = id }, OpenCodeJson.JsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // POST /session/{id}/message → record which session got the message
            if (request.Method == HttpMethod.Post && path.Contains("/message"))
            {
                // Extract session ID from path: /session/{id}/message
                var segments = path.Split('/');
                var sessionIdx = Array.IndexOf(segments, "session");
                if (sessionIdx >= 0 && sessionIdx + 1 < segments.Length)
                {
                    MessageSessionIds.Add(segments[sessionIdx + 1]);
                }

                var responseBody = JsonSerializer.Serialize(
                    new SendMessageResponse
                    {
                        Parts = [new MessagePart { Type = "text", Text = "review complete" }]
                    }, OpenCodeJson.JsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            }

            // GET /event (SSE) → return empty stream that closes immediately
            if (request.Method == HttpMethod.Get && path.Contains("/event"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/event-stream")
                };
            }

            // GET /session/{id} (token capture) → return minimal session info
            if (request.Method == HttpMethod.Get && path.Contains("/session/"))
            {
                var body = JsonSerializer.Serialize(new
                {
                    id = "unknown",
                    tokens = new { input = 0, output = 0, reasoning = 0 },
                    cost = 0.0
                }, OpenCodeJson.JsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Thread-safe client factory for concurrent tests.
    /// </summary>
    private sealed class ConcurrentMockClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public ConcurrentMockClientFactory(HttpMessageHandler handler)
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
}
