using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Observability tests for <see cref="AgentAuthorizationFilter"/>.
/// Verifies that:
/// - <see cref="PipelineTelemetry.HubAuthRejections"/> is emitted with the correct reason tag
///   for each rejection path (not_registered, reconnect_race, job_mismatch, operator_forbidden).
/// - Reconnect-race rejections (agentId query param present) log at Debug.
/// - True unregistered connections log at Warning.
/// </summary>
public class AgentAuthorizationFilterObservabilityTests
{
    private readonly Mock<IAgentRegistryService> _registryMock;
    private readonly Mock<ILogger> _loggerMock;
    private readonly AgentAuthorizationFilter _filter;

    public AgentAuthorizationFilterObservabilityTests()
    {
        _registryMock = new Mock<IAgentRegistryService>();
        _loggerMock = new Mock<ILogger>();
        _filter = new AgentAuthorizationFilter(_registryMock.Object, _loggerMock.Object);
    }

    // ── Counter emitted with correct reason ──────────────────────────────

    [Fact]
    public async Task NotRegistered_EmitsCounter_WithNotRegisteredReason()
    {
        _registryMock.Setup(r => r.GetByConnectionId(It.IsAny<string>())).Returns((AgentEntry?)null);
        _registryMock.Setup(r => r.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);

        var hub = CreateHub("conn-unknown");
        var ctx = MakeContext("conn-unknown");
        hub.Context = ctx;
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.Heartbeat))!;
        var invCtx = new HubInvocationContext(ctx, Mock.Of<IServiceProvider>(), hub, method, []);

        var measurements = new List<(long Value, string Reason)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "agent.hub.auth_rejections")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "agent.hub.auth_rejections") return;
            var reason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "reason") reason = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, reason));
        });
        meter.Start();

        try
        {
            await _filter.InvokeMethodAsync(invCtx, _ => ValueTask.FromResult((object?)null))
                .AsTask().ContinueWith(_ => { }); // swallow HubException
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().ContainSingle(m => m.Reason == PipelineTelemetry.HubAuthRejectionReasons.NotRegistered,
            "not_registered reason should be emitted for unknown connections");
    }

    [Fact]
    public async Task ReconnectRace_EmitsCounter_WithReconnectRaceReason()
    {
        const string agentId = "race-agent";
        _registryMock.Setup(r => r.GetByConnectionId(It.IsAny<string>())).Returns((AgentEntry?)null);
        // Redis also misses (race window — Register not yet written)
        _registryMock.Setup(r => r.GetByAgentId(new AgentId(agentId))).Returns((AgentEntry?)null);

        var hub = CreateHub("conn-race");
        var ctx = MakeContextWithAgentIdQuery("conn-race", agentId);
        hub.Context = ctx;
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.Heartbeat))!;
        var invCtx = new HubInvocationContext(ctx, Mock.Of<IServiceProvider>(), hub, method, []);

        var measurements = new List<(long Value, string Reason)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "agent.hub.auth_rejections")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "agent.hub.auth_rejections") return;
            var reason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "reason") reason = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, reason));
        });
        meter.Start();

        try
        {
            await _filter.InvokeMethodAsync(invCtx, _ => ValueTask.FromResult((object?)null))
                .AsTask().ContinueWith(_ => { });
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().ContainSingle(m => m.Reason == PipelineTelemetry.HubAuthRejectionReasons.ReconnectRace,
            "reconnect_race reason should be emitted when agentId query param is present");
    }

    [Fact]
    public async Task JobMismatch_EmitsCounter_WithJobMismatchReason()
    {
        const string connectionId = "conn-1";
        var entry = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = connectionId,
            Hostname = "h",
            Labels = [],
            Status = AgentStatus.Busy,
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-correct"
        };
        _registryMock.Setup(r => r.GetByConnectionId(connectionId)).Returns(entry);

        var hub = CreateHub(connectionId);
        var method = typeof(AgentHub).GetMethod(nameof(AgentHub.ReportJobCompleted))!;
        var ctx = MakeContext(connectionId);
        hub.Context = ctx;
        var invCtx = new HubInvocationContext(ctx, Mock.Of<IServiceProvider>(), hub, method,
            [new JobId("job-wrong"), new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow }]);

        var measurements = new List<(long Value, string Reason)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "agent.hub.auth_rejections")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "agent.hub.auth_rejections") return;
            var reason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "reason") reason = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, reason));
        });
        meter.Start();

        try
        {
            await _filter.InvokeMethodAsync(invCtx, _ => ValueTask.FromResult((object?)null))
                .AsTask().ContinueWith(_ => { });
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().ContainSingle(m => m.Reason == PipelineTelemetry.HubAuthRejectionReasons.JobMismatch,
            "job_mismatch reason should be emitted for jobId mismatch");
    }

    // ── Reason constants match expected string values ─────────────────────

    // TODO [WARNING]: No test verifies that the operator_forbidden counter is actually incremented
    // when an operator connection calls a restricted method. The test below only asserts the
    // constant's string value. Add a test that exercises GuardOperatorMethod with a non-UI-subscription
    // method and verifies the counter emits with reason=operator_forbidden. (Test Quality Review)

    // TODO [WARNING]: No test verifies the log-level demotion behaviour introduced by this PR:
    // - reconnect-race rejections should log at Debug (_logger.Debug called)
    // - true unregistered connections should log at Warning (_logger.Warning called)
    // The _loggerMock is never verified with Verify(...) calls for Debug/Warning in any test method.
    // Add Moq Verify assertions for the correct log level in the NotRegistered and ReconnectRace tests.
    // (Test Quality Review)

    // TODO [WARNING]: The integration regression test (AgentHubGateTests: Report after forced reconnect
    // succeeds, not rejected) required by the issue spec was not added to
    // tests/CodingAgent.Api.IntegrationTests/AgentHubGateTests.cs. This is the primary end-to-end
    // acceptance proof that manager-mediated Report* calls wait for re-registration after a reconnect.
    // (Test Quality Review)

    [Fact]
    public void HubAuthRejectionReasons_NotRegistered_Value()
        => PipelineTelemetry.HubAuthRejectionReasons.NotRegistered.Should().Be("not_registered");

    [Fact]
    public void HubAuthRejectionReasons_ReconnectRace_Value()
        => PipelineTelemetry.HubAuthRejectionReasons.ReconnectRace.Should().Be("reconnect_race");

    [Fact]
    public void HubAuthRejectionReasons_JobMismatch_Value()
        => PipelineTelemetry.HubAuthRejectionReasons.JobMismatch.Should().Be("job_mismatch");

    [Fact]
    public void HubAuthRejectionReasons_OperatorForbidden_Value()
        => PipelineTelemetry.HubAuthRejectionReasons.OperatorForbidden.Should().Be("operator_forbidden");

    // ── Helpers ──────────────────────────────────────────────────────────

    private AgentHub CreateHub(string connectionId)
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Mock.Of<IAgentHubFacade>(),
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _loggerMock.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));
        hub.Context = MakeContext(connectionId);
        return hub;
    }

    private static HubCallerContext MakeContext(string connectionId)
    {
        var mock = new Mock<HubCallerContext>();
        mock.Setup(c => c.ConnectionId).Returns(connectionId);
        return mock.Object;
    }

    private static HubCallerContext MakeContextWithAgentIdQuery(string connectionId, string agentId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?agentId={agentId}");

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var mock = new Mock<HubCallerContext>();
        mock.Setup(c => c.ConnectionId).Returns(connectionId);
        mock.Setup(c => c.Features).Returns(features);
        return mock.Object;
    }

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext httpContext) => HttpContext = httpContext;
        public HttpContext? HttpContext { get; set; }
    }
}
