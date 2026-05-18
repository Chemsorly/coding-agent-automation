using System.Net;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Example-based tests for factory integration, ValidateAsync, KillAsync,
/// GetLatestSessionIdAsync, DisposeAsync, and health monitor.
/// Feature: opencode-agent-executor
/// **Validates: Requirements 6.1, 6.4, 6.5, 6.7, 1.7**
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
public class OpenCodeFactoryAndLifecycleTests
{
    // ── ValidateAsync Tests ─────────────────────────────────────────────

    /// <summary>
    /// ValidateAsync succeeds when the health endpoint returns 200 with healthy: true.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task ValidateAsync_HealthyResponse_Succeeds()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueHealthy(ctx.Handler, "2.0.0");

        // Act & Assert — should not throw
        await ctx.Provider.ValidateAsync(CancellationToken.None);

        // Verify GET /global/health was called
        Assert.Single(ctx.Handler.Requests);
        Assert.Equal(HttpMethod.Get, ctx.Handler.Requests[0].Method);
        Assert.Equal("/global/health", ctx.Handler.Requests[0].Path);
    }

    /// <summary>
    /// ValidateAsync throws InvalidOperationException when the health endpoint returns unhealthy.
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Fact]
    public async Task ValidateAsync_UnhealthyResponse_ThrowsInvalidOperationException()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        ctx.Handler.EnqueueJsonResponse(new HealthResponse { Healthy = false, Version = "1.0.0" });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Provider.ValidateAsync(CancellationToken.None));

        Assert.Contains("not healthy", ex.Message);
    }

    /// <summary>
    /// ValidateAsync throws InvalidOperationException when the health endpoint returns HTTP error.
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Fact]
    public async Task ValidateAsync_HttpError_ThrowsInvalidOperationException()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        ctx.Handler.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "server down");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Provider.ValidateAsync(CancellationToken.None));

        Assert.Contains("unhealthy response", ex.Message);
    }

    /// <summary>
    /// ValidateAsync throws InvalidOperationException on timeout (simulated via network error).
    /// **Validates: Requirements 6.5**
    /// </summary>
    [Fact]
    public async Task ValidateAsync_NetworkError_ThrowsInvalidOperationException()
    {
        // Arrange — use a handler that throws HttpRequestException (simulates unreachable server)
        var handler = new HealthNetworkErrorHandler();
        var factory = new HealthNetworkErrorClientFactory(handler);
        var loggerMock = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, loggerMock.Object);

        // Act & Assert — should throw InvalidOperationException wrapping the network error
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ValidateAsync(CancellationToken.None));

        Assert.Contains("unreachable", ex.Message);
    }

    // ── KillAsync Tests ─────────────────────────────────────────────────

    /// <summary>
    /// KillAsync sends POST /session/:id/abort when a session is active.
    /// **Validates: Requirements 1.7**
    /// </summary>
    [Fact]
    public async Task KillAsync_SessionActive_SendsAbort()
    {
        // Arrange — establish a session first
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "session-to-kill");
        await ctx.Provider.EnsureSessionAsync("/tmp/workspace", CancellationToken.None);

        // Enqueue response for the abort call
        ctx.Handler.EnqueueOk();

        // Act
        await ctx.Provider.KillAsync();

        // Assert — verify POST /session/session-to-kill/abort was called
        var abortRequest = ctx.Handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, abortRequest.Method);
        Assert.Equal("/session/session-to-kill/abort", abortRequest.Path);
    }

    /// <summary>
    /// KillAsync is a no-op when no session is active (no HTTP calls made).
    /// **Validates: Requirements 1.7**
    /// </summary>
    [Fact]
    public async Task KillAsync_NoSession_IsNoOp()
    {
        // Arrange — fresh provider with no session
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Act
        await ctx.Provider.KillAsync();

        // Assert — no HTTP requests should have been made
        Assert.Empty(ctx.Handler.Requests);
    }

    /// <summary>
    /// KillAsync catches and logs exceptions (best-effort).
    /// **Validates: Requirements 1.7**
    /// </summary>
    [Fact]
    public async Task KillAsync_HttpError_DoesNotThrow()
    {
        // Arrange — establish a session first
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "session-error");
        await ctx.Provider.EnsureSessionAsync("/tmp/workspace", CancellationToken.None);

        // Enqueue error response for abort
        ctx.Handler.EnqueueResponse(HttpStatusCode.InternalServerError, "abort failed");

        // Act & Assert — should not throw
        await ctx.Provider.KillAsync();
    }

    // ── GetLatestSessionIdAsync Tests ───────────────────────────────────

    /// <summary>
    /// GetLatestSessionIdAsync returns stored session ID after session creation.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task GetLatestSessionIdAsync_AfterSessionCreation_ReturnsStoredId()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "stored-session-123");
        await ctx.Provider.EnsureSessionAsync("/tmp/workspace", CancellationToken.None);

        // Act
        var sessionId = await ctx.Provider.GetLatestSessionIdAsync("/tmp/workspace", CancellationToken.None);

        // Assert
        Assert.Equal("stored-session-123", sessionId);
    }

    /// <summary>
    /// GetLatestSessionIdAsync returns null when no session has been created.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task GetLatestSessionIdAsync_NoSession_ReturnsNull()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Act
        var sessionId = await ctx.Provider.GetLatestSessionIdAsync("/tmp/workspace", CancellationToken.None);

        // Assert
        Assert.Null(sessionId);
    }

    // ── DisposeAsync Tests ──────────────────────────────────────────────

    /// <summary>
    /// DisposeAsync clears the session ID without making HTTP calls.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ClearsSessionWithoutHttpCalls()
    {
        // Arrange — establish a session first
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "session-to-dispose");
        await ctx.Provider.EnsureSessionAsync("/tmp/workspace", CancellationToken.None);

        // Verify session exists
        var sessionBefore = await ctx.Provider.GetLatestSessionIdAsync("/tmp/workspace", CancellationToken.None);
        Assert.Equal("session-to-dispose", sessionBefore);

        var requestCountBefore = ctx.Handler.Requests.Count;

        // Act
        await ctx.Provider.DisposeAsync();

        // Assert — session should be cleared
        var sessionAfter = await ctx.Provider.GetLatestSessionIdAsync("/tmp/workspace", CancellationToken.None);
        Assert.Null(sessionAfter);

        // Assert — no additional HTTP calls were made during dispose
        Assert.Equal(requestCountBefore, ctx.Handler.Requests.Count);
    }

    // ── Factory Tests ───────────────────────────────────────────────────

    /// <summary>
    /// Factory creates OpenCodeAgentProvider for "OpenCode" provider type.
    /// **Validates: Requirements 6.1**
    /// </summary>
    [Fact]
    public void Factory_OpenCodeType_CreatesOpenCodeAgentProvider()
    {
        // Arrange — set the required env var
        var originalPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", "test-password-32-chars-long-enough");

            var orchestratorMock = new Mock<IKiroCliOrchestrator>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4096") });

            var pipelineConfig = new PipelineConfiguration();
            var factory = new AgentProviderFactory(
                orchestratorMock.Object, httpClientFactoryMock.Object, pipelineConfig);

            var config = new ProviderConfig
            {
                Kind = ProviderKind.Agent,
                ProviderType = "OpenCode",
                DisplayName = "Test OpenCode Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.BaseUrl] = "http://127.0.0.1:4096"
                }
            };

            // Act
            var provider = factory.CreateAgentProvider(config);

            // Assert
            Assert.NotNull(provider);
            Assert.IsType<OpenCodeAgentProvider>(provider);
            Assert.Equal(AgentProviderType.OpenCode, provider.ProviderType);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", originalPassword);
        }
    }

    /// <summary>
    /// Factory recognizes "OpenCode" case-insensitively.
    /// **Validates: Requirements 6.1**
    /// </summary>
    [Fact]
    public void Factory_OpenCodeTypeCaseInsensitive_CreatesProvider()
    {
        // Arrange
        var originalPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", "test-password-32-chars-long-enough");

            var orchestratorMock = new Mock<IKiroCliOrchestrator>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:4096") });

            var pipelineConfig = new PipelineConfiguration();
            var factory = new AgentProviderFactory(
                orchestratorMock.Object, httpClientFactoryMock.Object, pipelineConfig);

            var config = new ProviderConfig
            {
                Kind = ProviderKind.Agent,
                ProviderType = "opencode",
                DisplayName = "Test OpenCode Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.BaseUrl] = "http://127.0.0.1:4096"
                }
            };

            // Act
            var provider = factory.CreateAgentProvider(config);

            // Assert
            Assert.NotNull(provider);
            Assert.IsType<OpenCodeAgentProvider>(provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", originalPassword);
        }
    }

    /// <summary>
    /// Factory throws ArgumentException for invalid baseUrl.
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://invalid-scheme.com")]
    [InlineData("")]
    public void Factory_InvalidBaseUrl_ThrowsArgumentException(string invalidUrl)
    {
        // Arrange
        var originalPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", "test-password-32-chars-long-enough");

            var orchestratorMock = new Mock<IKiroCliOrchestrator>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            var pipelineConfig = new PipelineConfiguration();
            var factory = new AgentProviderFactory(
                orchestratorMock.Object, httpClientFactoryMock.Object, pipelineConfig);

            var config = new ProviderConfig
            {
                Kind = ProviderKind.Agent,
                ProviderType = "OpenCode",
                DisplayName = "Test OpenCode Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.BaseUrl] = invalidUrl
                }
            };

            // Act & Assert
            Assert.Throws<ArgumentException>(() => factory.CreateAgentProvider(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", originalPassword);
        }
    }

    /// <summary>
    /// Factory throws InvalidOperationException when OPENCODE_SERVER_PASSWORD is missing.
    /// **Validates: Requirements 6.7**
    /// </summary>
    [Fact]
    public void Factory_MissingPassword_ThrowsInvalidOperationException()
    {
        // Arrange — ensure env var is NOT set
        var originalPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", null);

            var orchestratorMock = new Mock<IKiroCliOrchestrator>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            var pipelineConfig = new PipelineConfiguration();
            var factory = new AgentProviderFactory(
                orchestratorMock.Object, httpClientFactoryMock.Object, pipelineConfig);

            var config = new ProviderConfig
            {
                Kind = ProviderKind.Agent,
                ProviderType = "OpenCode",
                DisplayName = "Test OpenCode Provider",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.BaseUrl] = "http://127.0.0.1:4096"
                }
            };

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => factory.CreateAgentProvider(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", originalPassword);
        }
    }

    // ── Health Monitor Tests ────────────────────────────────────────────

    /// <summary>
    /// Health monitor CheckHealthAsync succeeds on healthy response (GET /global/health called).
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task HealthMonitor_HealthyResponse_CompletesSuccessfully()
    {
        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var loggerMock = new Mock<ILogger>();

        var monitor = new OpenCodeHealthMonitor(factory, loggerMock.Object);

        // Enqueue healthy response
        OpenCodeTestHelpers.EnqueueHealthy(handler, "2.0.0");

        // Act — invoke CheckHealthAsync directly (internal method)
        await monitor.CheckHealthInternalAsync(CancellationToken.None);

        // Assert — GET /global/health was called
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/global/health", handler.Requests[0].Path);
    }

    /// <summary>
    /// Health monitor CheckHealthAsync logs warning on unhealthy response.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task HealthMonitor_UnhealthyResponse_LogsWarning()
    {
        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var loggerMock = new Mock<ILogger>();

        var monitor = new OpenCodeHealthMonitor(factory, loggerMock.Object);

        // Enqueue unhealthy response (healthy: false)
        handler.EnqueueJsonResponse(new HealthResponse { Healthy = false, Version = "1.0.0" });

        // Act
        await monitor.CheckHealthInternalAsync(CancellationToken.None);

        // Assert — warning should be logged (Serilog uses Warning(string) overload for unhealthy state)
        loggerMock.Verify(
            l => l.Warning("OpenCode health check returned unhealthy state"),
            Times.Once);
    }

    /// <summary>
    /// Health monitor CheckHealthAsync logs warning on HTTP error status.
    /// **Validates: Requirements 6.4**
    /// </summary>
    [Fact]
    public async Task HealthMonitor_HttpError_LogsWarning()
    {
        // Arrange
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var loggerMock = new Mock<ILogger>();

        var monitor = new OpenCodeHealthMonitor(factory, loggerMock.Object);

        // Enqueue error response
        handler.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "server down");

        // Act
        await monitor.CheckHealthInternalAsync(CancellationToken.None);

        // Assert — GET /global/health was called and warning was logged
        // The health monitor calls Warning<int>("... {StatusCode}", 503) which is a generic overload
        Assert.Single(handler.Requests);
        Assert.Equal("/global/health", handler.Requests[0].Path);

        // Verify the generic Warning<int> overload was called
        loggerMock.Verify(
            l => l.Warning(It.Is<string>(s => s.Contains("StatusCode")), It.IsAny<int>()),
            Times.Once);
    }
}

// ── Custom handler for network error testing ────────────────────────────────

/// <summary>
/// A handler that throws HttpRequestException to simulate an unreachable server.
/// </summary>
internal sealed class HealthNetworkErrorHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        throw new HttpRequestException("Connection refused: simulated network error");
    }
}

/// <summary>
/// A factory that creates HttpClients backed by a HealthNetworkErrorHandler.
/// </summary>
internal sealed class HealthNetworkErrorClientFactory : IHttpClientFactory
{
    private readonly HealthNetworkErrorHandler _handler;

    public HealthNetworkErrorClientFactory(HealthNetworkErrorHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://127.0.0.1:4096")
        };
    }
}
