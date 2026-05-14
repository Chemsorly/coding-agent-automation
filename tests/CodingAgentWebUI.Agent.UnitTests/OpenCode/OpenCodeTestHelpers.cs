using System.Net;
using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Helper methods and factory methods for OpenCode provider tests.
/// Provides common setup patterns to reduce boilerplate across test classes.
/// </summary>
public static class OpenCodeTestHelpers
{
    /// <summary>
    /// Creates an <see cref="OpenCodeAgentProvider"/> backed by the given mock factory.
    /// Uses a null logger by default (falls back to Serilog.Log.Logger internally).
    /// </summary>
    public static OpenCodeAgentProvider CreateProvider(
        MockOpenCodeClientFactory factory,
        ILogger? logger = null,
        string? model = null)
    {
        return new OpenCodeAgentProvider(factory, logger, model);
    }

    /// <summary>
    /// Creates a fully wired test context: mock handler, factory, and provider.
    /// </summary>
    public static OpenCodeTestContext CreateTestContext(string? model = null)
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object, model);
        return new OpenCodeTestContext(handler, factory, logger, provider);
    }

    /// <summary>
    /// Enqueues a successful session creation response with the given session ID.
    /// </summary>
    public static void EnqueueSessionCreated(MockOpenCodeHandler handler, string sessionId = "test-session-001")
    {
        handler.EnqueueJsonResponse(new CreateSessionResponse { Id = sessionId });
    }

    /// <summary>
    /// Enqueues a successful message response with the given text parts.
    /// </summary>
    public static void EnqueueMessageResponse(MockOpenCodeHandler handler, params string[] textParts)
    {
        var parts = textParts.Select(t => new MessagePart { Type = "text", Text = t }).ToList();
        handler.EnqueueJsonResponse(new SendMessageResponse { Parts = parts });
    }

    /// <summary>
    /// Enqueues a successful health check response.
    /// </summary>
    public static void EnqueueHealthy(MockOpenCodeHandler handler, string version = "1.0.0")
    {
        handler.EnqueueJsonResponse(new HealthResponse { Healthy = true, Version = version });
    }

    /// <summary>
    /// Enqueues a session validation response (GET /session/:id returns 200).
    /// </summary>
    public static void EnqueueSessionValid(MockOpenCodeHandler handler)
    {
        handler.EnqueueOk("{\"id\":\"test-session-001\",\"status\":\"active\"}");
    }

    /// <summary>
    /// Enqueues a session not found response (GET /session/:id returns 404).
    /// </summary>
    public static void EnqueueSessionNotFound(MockOpenCodeHandler handler)
    {
        handler.EnqueueResponse(HttpStatusCode.NotFound, "{\"error\":\"session not found\"}");
    }

    /// <summary>
    /// Enqueues a diff response with the given file diffs.
    /// </summary>
    public static void EnqueueDiffResponse(MockOpenCodeHandler handler, params FileDiff[] diffs)
    {
        var json = JsonSerializer.Serialize(diffs, OpenCodeJson.JsonOptions);
        handler.EnqueueResponse(HttpStatusCode.OK, json);
    }

    /// <summary>
    /// Creates a minimal <see cref="AgentRequest"/> for testing.
    /// </summary>
    public static AgentRequest CreateRequest(
        string prompt = "test prompt",
        string? workspacePath = null,
        bool useResume = false,
        string? resumeSessionId = null,
        TimeSpan? timeout = null)
    {
        return new AgentRequest
        {
            Prompt = prompt,
            WorkspacePath = workspacePath ?? Path.GetTempPath(),
            UseResume = useResume,
            ResumeSessionId = resumeSessionId,
            Timeout = timeout ?? TimeSpan.FromMinutes(5)
        };
    }
}

/// <summary>
/// Bundles all test dependencies for an OpenCode provider test.
/// Provides convenient access to the handler, factory, logger mock, and provider.
/// </summary>
public sealed class OpenCodeTestContext
{
    public MockOpenCodeHandler Handler { get; }
    public MockOpenCodeClientFactory Factory { get; }
    public Mock<ILogger> LoggerMock { get; }
    public OpenCodeAgentProvider Provider { get; }

    public OpenCodeTestContext(
        MockOpenCodeHandler handler,
        MockOpenCodeClientFactory factory,
        Mock<ILogger> loggerMock,
        OpenCodeAgentProvider provider)
    {
        Handler = handler;
        Factory = factory;
        LoggerMock = loggerMock;
        Provider = provider;
    }
}
