using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>The hub hands an agent call's usage readings to the usage threshold, without needing the run on this replica.</summary>
public sealed class AgentHubRateLimitReadingTests
{
    private static readonly Guid WorkItem = Guid.Parse("4f1a0000-0000-0000-0000-000000004151");
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ICredentialUsageObserver> _observer = new();

    private static readonly IReadOnlyList<AgentRateLimitObservation> Readings =
    [
        new() { Provider = "claude", Window = "five_hour", Status = "allowed", Utilization = 0.76 },
    ];

    private AgentHub Hub(ICredentialUsageObserver? observer)
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _facade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext(),
            UsageObserver: observer));
        hub.Context = Mock.Of<HubCallerContext>(c => c.ConnectionId == "conn-usage");
        return hub;
    }

    [Fact]
    public async Task RateLimitReading_ShouldPassTheReadingsOfTheWorkItemToTheObserver_EvenWithoutTheRunTracked()
    {
        await Hub(_observer.Object).ReportPipelineRunEvent(WorkItem.ToString(),
            new PipelineRunEventReport { Kind = PipelineRunEventKind.RateLimitReading, RateLimits = Readings });

        _observer.Verify(o => o.ObserveAsync(WorkItem, Readings, It.IsAny<CancellationToken>()), Times.Once);
        _facade.Verify(f => f.GetRun(It.IsAny<JobId>()), Times.Never);
    }

    [Fact]
    public async Task RateLimitReading_WhenTheObserverFails_ShouldNotThrowToTheAgent()
    {
        _observer.Setup(o => o.ObserveAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<AgentRateLimitObservation>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => Hub(_observer.Object).ReportPipelineRunEvent(WorkItem.ToString(),
            new PipelineRunEventReport { Kind = PipelineRunEventKind.RateLimitReading, RateLimits = Readings });

        await act.Should().NotThrowAsync();
    }

    private void ChatPodOwns(string sessionId)
    {
        AgentEntry Entry() => new()
        {
            AgentId = new AgentId("chat-pod-1"), ConnectionId = "conn-usage", Hostname = "chat-pod-1",
            Labels = ["claude"], RegisteredAt = DateTimeOffset.UtcNow, ActiveChatSessionId = sessionId,
        };
        _facade.Setup(f => f.GetByConnectionId("conn-usage")).Returns(Entry());
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("chat-pod-1"), It.IsAny<CancellationToken>())).ReturnsAsync(Entry());
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ChatCompleted_WithReadings_ShouldCountThemForTheChatsCredential()
    {
        ChatPodOwns("s1");

        await Hub(_observer.Object).ReportChatCompleted(new ChatCompletedMessage
        {
            SessionId = "s1", ExitCode = 0, Credential = AgentCredentials.ClaudeSubscription, RateLimits = Readings,
        });

        _observer.Verify(o => o.ObserveCredentialAsync(AgentCredentials.ClaudeSubscription, Readings, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChatCompleted_WithoutReadings_ShouldObserveNothing()
    {
        ChatPodOwns("s1");

        await Hub(_observer.Object).ReportChatCompleted(new ChatCompletedMessage { SessionId = "s1", ExitCode = 0 });

        _observer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RateLimitReading_WithoutReadingsOrObserver_ShouldDoNothing()
    {
        await Hub(_observer.Object).ReportPipelineRunEvent(WorkItem.ToString(),
            new PipelineRunEventReport { Kind = PipelineRunEventKind.RateLimitReading });
        await Hub(observer: null).ReportPipelineRunEvent(WorkItem.ToString(),
            new PipelineRunEventReport { Kind = PipelineRunEventKind.RateLimitReading, RateLimits = Readings });

        _observer.VerifyNoOtherCalls();
    }
}
