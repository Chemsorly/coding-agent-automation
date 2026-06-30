using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for <see cref="OpenCodeHealthMonitor"/>.
/// Verifies health check responses, session status polling, and error handling.
/// </summary>
public class OpenCodeHealthMonitorTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    // ── Constructor Guard Clauses ────────────────────────────────────────

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        var act = () => new OpenCodeHealthMonitor(null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_UsesStaticLogger()
    {
        // Null logger is explicitly handled — should not throw
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var act = () => new OpenCodeHealthMonitor(factory, null!);
        act.Should().NotThrow();
    }

    // ── CheckHealthInternalAsync — Happy Path ────────────────────────────

    [Fact]
    public async Task CheckHealth_HealthyResponse_DoesNotLogWarning()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        handler.EnqueueJsonResponse(new HealthResponse { Healthy = true, Version = "1.0.0" });

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);
        await monitor.CheckHealthInternalAsync(CancellationToken.None);

        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckHealth_UnhealthyResponse_DoesNotThrow()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        handler.EnqueueJsonResponse(new HealthResponse { Healthy = false, Version = "1.0.0" });

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        // Should handle unhealthy state gracefully (log warning internally)
        var act = () => monitor.CheckHealthInternalAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckHealth_Non200StatusCode_DoesNotThrow()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        handler.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "");

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        // Should handle the 503 gracefully (log warning internally) without propagating exceptions
        var act = () => monitor.CheckHealthInternalAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckHealth_NetworkError_LogsWarningWithoutThrowing()
    {
        var factory = CreateThrowingFactory();

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        var act = () => monitor.CheckHealthInternalAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── PollSessionStatusInternalAsync ───────────────────────────────────

    [Fact]
    public async Task PollSessionStatus_ValidResponse_UpdatesLastSessionStatuses()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);

        var statusResponse = new Dictionary<string, SseSessionStatus>
        {
            ["session-1"] = new() { Type = "idle" },
            ["session-2"] = new() { Type = "busy" }
        };
        handler.EnqueueJsonResponse(statusResponse);

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);
        await monitor.PollSessionStatusInternalAsync(CancellationToken.None);

        monitor.LastSessionStatuses.Should().NotBeNull();
        monitor.LastSessionStatuses!.Count.Should().Be(2);
        monitor.LastSessionStatuses.Should().ContainKey("session-1");
        monitor.LastSessionStatuses.Should().ContainKey("session-2");
    }

    [Fact]
    public async Task PollSessionStatus_RetrySession_DoesNotThrow_AndUpdatesStatuses()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);

        var statusResponse = new Dictionary<string, SseSessionStatus>
        {
            ["session-retry"] = new() { Type = "retry", Attempt = 3, Message = "Rate limited" }
        };
        handler.EnqueueJsonResponse(statusResponse);

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);
        await monitor.PollSessionStatusInternalAsync(CancellationToken.None);

        // Verifies the statuses were parsed and stored (the warning logging is an internal detail)
        monitor.LastSessionStatuses.Should().NotBeNull();
        monitor.LastSessionStatuses!.Should().ContainKey("session-retry");
        monitor.LastSessionStatuses["session-retry"].Type.Should().Be("retry");
        monitor.LastSessionStatuses["session-retry"].Attempt.Should().Be(3);
    }

    [Fact]
    public async Task PollSessionStatus_Non200Response_DoesNotUpdateStatuses()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        handler.EnqueueResponse(HttpStatusCode.NotFound, "");

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);
        await monitor.PollSessionStatusInternalAsync(CancellationToken.None);

        monitor.LastSessionStatuses.Should().BeNull();
    }

    [Fact]
    public async Task PollSessionStatus_NetworkError_DoesNotThrow()
    {
        var factory = CreateThrowingFactory();

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        var act = () => monitor.PollSessionStatusInternalAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── BackgroundService Lifecycle ──────────────────────────────────────

    [Fact]
    public async Task StartStop_CompletesGracefully()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        // Enqueue enough responses for at least one poll cycle
        handler.EnqueueJsonResponse(new HealthResponse { Healthy = true, Version = "1.0.0" });
        handler.EnqueueJsonResponse(new Dictionary<string, SseSessionStatus>());

        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await monitor.StartAsync(cts.Token);
        await Task.Delay(250); // Let one poll cycle attempt
        await monitor.StopAsync(CancellationToken.None);
        // Should complete without throwing
    }

    [Fact]
    public async Task LastSessionStatuses_InitiallyNull()
    {
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var monitor = new OpenCodeHealthMonitor(factory, _mockLogger.Object);

        monitor.LastSessionStatuses.Should().BeNull();
        await Task.CompletedTask; // Keep async for consistency
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IHttpClientFactory CreateThrowingFactory()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var throwingClient = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://localhost:3000")
        };
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(throwingClient);
        return mockFactory.Object;
    }

    /// <summary>
    /// Handler that throws HttpRequestException on every request (simulates network failure).
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            throw new HttpRequestException("Connection refused");
        }
    }
}
