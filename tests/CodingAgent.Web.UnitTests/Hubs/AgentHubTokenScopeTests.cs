using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Tests for the server-side token scope decision in <see cref="AgentHub.RequestTokenRefresh"/>:
/// only a refactoring scan's repository token may hold issue permission.
/// </summary>
public sealed class AgentHubTokenScopeTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly Mock<IAgentTokenRefreshService> _tokens = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: _tokens.Object,
            Logger: _logger.Object,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext(),
            TriageOps: null));
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = ctx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private void SetupTokens() =>
        _tokens.Setup(t => t.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new TokenRefreshResponse { Token = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

    // ── Permission matrix ─────────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderKind.Repository, ConsolidationRunType.RefactoringDetection, true)]
    [InlineData(ProviderKind.Repository, ConsolidationRunType.BrainConsolidation, false)]
    [InlineData(ProviderKind.Repository, null, false)]
    [InlineData(ProviderKind.Brain, ConsolidationRunType.RefactoringDetection, false)]
    [InlineData(ProviderKind.Issue, ConsolidationRunType.RefactoringDetection, false)]
    [InlineData(ProviderKind.Pipeline, ConsolidationRunType.RefactoringDetection, false)]
    [InlineData(ProviderKind.Agent, ConsolidationRunType.RefactoringDetection, false)]
    public async Task RequestTokenRefresh_GrantsIssuePermission_OnlyForARefactoringScansRepositoryToken(
        ProviderKind kind, ConsolidationRunType? runType, bool expected)
    {
        _facade.Setup(f => f.GetConsolidationRunTypeAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(runType);
        SetupTokens();

        await CreateHub().RequestTokenRefresh(new JobId("job-1"), kind, includeIssuePermission: true);

        _tokens.Verify(t => t.RefreshTokenAsync("job-1", kind, It.IsAny<CancellationToken>(), expected), Times.Once);
    }

    // ── Short-circuit when permission not requested ───────────────────────

    [Fact]
    public async Task RequestTokenRefresh_WithoutThePermission_DoesNotLookUpTheRunType()
    {
        SetupTokens();

        await CreateHub().RequestTokenRefresh(new JobId("job-1"), ProviderKind.Repository);

        _facade.Verify(f => f.GetConsolidationRunTypeAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(t => t.RefreshTokenAsync("job-1", ProviderKind.Repository, It.IsAny<CancellationToken>(), false), Times.Once);
    }

    // ── Lookup failure propagates ─────────────────────────────────────────

    [Fact]
    public async Task RequestTokenRefresh_WhenTheLookupFails_HandsOutNoToken()
    {
        _facade.Setup(f => f.GetConsolidationRunTypeAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("connection refused"));

        var act = () => CreateHub().RequestTokenRefresh(new JobId("job-1"), ProviderKind.Repository, includeIssuePermission: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _tokens.Verify(t => t.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }
}

/// <summary>
/// Counter tests for <see cref="PipelineTelemetry.TokenVendingScopeRefused"/>.
/// Runs in the "Metrics" collection to serialize with other counter tests that read process-global state.
/// </summary>
[Collection("Metrics")]
public class AgentHubTokenScopeTelemetryTests
{
    /// <summary>
    /// Marks the async flow of the test that is listening. <see cref="MeterListener"/> callbacks run
    /// synchronously on the flow that calls Add, so a measurement seen under any other value was emitted
    /// by a test running in parallel and must be ignored.
    /// </summary>
    private static readonly AsyncLocal<object?> TestFlow = new();

    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly Mock<IAgentTokenRefreshService> _tokens = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: _tokens.Object,
            Logger: _logger.Object,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext(),
            TriageOps: null));
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = ctx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private void SetupTokens() =>
        _tokens.Setup(t => t.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new TokenRefreshResponse { Token = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

    [Fact]
    public async Task RequestTokenRefresh_WhenRefused_CountsTheRefusal()
    {
        // Arrange: a non-refactoring run asking for issue permission → will be refused
        _facade.Setup(f => f.GetConsolidationRunTypeAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((ConsolidationRunType?)null);
        SetupTokens();

        var measurements = new List<(long Value, string Permission)>();
        var flow = new object();
        TestFlow.Value = flow;
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "token_vending.scope_refused")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "token_vending.scope_refused" || !ReferenceEquals(TestFlow.Value, flow)) return;
            var permission = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "permission") permission = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, permission));
        });
        meter.Start();

        try
        {
            await CreateHub().RequestTokenRefresh(new JobId("job-1"), ProviderKind.Repository, includeIssuePermission: true);
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().ContainSingle(m => m.Value == 1 && m.Permission == "issues",
            "one refusal with permission=issues should be emitted");
    }

    [Fact]
    public async Task RequestTokenRefresh_WhenAllowed_CountsNothing()
    {
        // Arrange: a refactoring scan requesting its repository token → allowed, no counter
        _facade.Setup(f => f.GetConsolidationRunTypeAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(ConsolidationRunType.RefactoringDetection);
        SetupTokens();

        var measurements = new List<long>();
        var flow = new object();
        TestFlow.Value = flow;
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "token_vending.scope_refused")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "token_vending.scope_refused" || !ReferenceEquals(TestFlow.Value, flow)) return;
            measurements.Add(value);
        });
        meter.Start();

        try
        {
            await CreateHub().RequestTokenRefresh(new JobId("job-1"), ProviderKind.Repository, includeIssuePermission: true);
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().BeEmpty("no refusal counter should be emitted when permission is granted");
    }
}
