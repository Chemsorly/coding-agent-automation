using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="AgentHub"/> hub methods.
/// Uses Moq to mock all interface dependencies and sets Hub.Context / Hub.Groups
/// via their public property setters (ASP.NET Core 10+).
/// </summary>
public sealed class AgentHubMethodTests
{
    // ── Shared fixture ──────────────────────────────────────────────────

    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IAgentJobLifecycleService> _lifecycleService = new();
    private readonly Mock<IAgentTokenRefreshService> _tokenRefreshService = new();
    private readonly Mock<IGateCommentFormatter> _gateCommentFormatter = new();
    private readonly Mock<IAgentOrphanRecoveryService> _orphanRecoveryService = new();
    private readonly Mock<IHubContext<AgentHub>> _uiContext = new();
    private readonly Mock<IClientProxy> _uiClientProxy = new();
    private readonly Mock<IHubClients> _uiClients = new();
    private readonly Mock<HubCallerContext> _hubCallerContext = new();
    private readonly Mock<IGroupManager> _groupManager = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly ModelFetchService _modelFetchService;

    public AgentHubMethodTests()
    {
        // ModelFetchService is sealed concrete — create with minimal real deps.
        var registry = new AgentRegistryService(Log.Logger);
        var agentComm = new Mock<IAgentCommunication>();
        _modelFetchService = new ModelFetchService(registry, agentComm.Object, Log.Logger);

        // Wire up IHubContext broadcast chain: uiContext.Clients.Group(...) → uiClientProxy
        _uiClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_uiClientProxy.Object);
        _uiContext.Setup(c => c.Clients).Returns(_uiClients.Object);

        // Default HubCallerContext connection id
        _hubCallerContext.Setup(c => c.ConnectionId).Returns("agent-conn-1");
    }

    private AgentHub CreateHub()
    {
        var deps = new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ModelFetchService: _modelFetchService,
            ConsolidationService: _consolidationService.Object,
            BadgeService: _badgeService,
            IssueOps: _issueOps.Object,
            LifecycleService: _lifecycleService.Object,
            TokenRefreshService: _tokenRefreshService.Object,
            GateCommentFormatter: _gateCommentFormatter.Object,
            Logger: Log.Logger,
            OrphanRecoveryService: _orphanRecoveryService.Object,
            UiContext: _uiContext.Object);

        var hub = new AgentHub(deps);
        hub.Context = _hubCallerContext.Object;
        hub.Groups = _groupManager.Object;
        return hub;
    }

    private static AgentEntry CreateAgentEntry(string agentId, string connectionId,
        string? activeChatSessionId = null, string? activeJobId = null) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "k8s-pod",
        Labels = [],
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveChatSessionId = activeChatSessionId,
        ActiveJobId = activeJobId,
    };

    // ── AgentHub.Chat ──────────────────────────────────────────────────

    [Fact]
    public async Task SubscribeToChatSession_CallsAddToGroupAsync()
    {
        _groupManager
            .Setup(g => g.AddToGroupAsync("agent-conn-1", "chat-session-my-session", default))
            .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.SubscribeToChatSession("my-session");

        _groupManager.Verify(g => g.AddToGroupAsync("agent-conn-1", "chat-session-my-session", default), Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromChatSession_CallsRemoveFromGroupAsync()
    {
        _groupManager
            .Setup(g => g.RemoveFromGroupAsync("agent-conn-1", "chat-session-my-session", default))
            .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.UnsubscribeFromChatSession("my-session");

        _groupManager.Verify(g => g.RemoveFromGroupAsync("agent-conn-1", "chat-session-my-session", default), Times.Once);
    }

    [Fact]
    public async Task ReportChatResponse_AgentOwnsSession_BroadcastsToUiGroup()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeChatSessionId: "session-abc");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);

        _uiClientProxy
            .Setup(p => p.SendCoreAsync(HubMethodNames.OnChatResponse, It.IsAny<object?[]>(), default))
            .Returns(Task.CompletedTask);

        var message = new ChatResponseMessage
        {
            SessionId = "session-abc",
            Lines = ["line1", "line2"],
        };

        var hub = CreateHub();
        await hub.ReportChatResponse(message);

        _uiClients.Verify(c => c.Group("chat-session-session-abc"), Times.Once);
        _uiClientProxy.Verify(p => p.SendCoreAsync(
            HubMethodNames.OnChatResponse,
            It.Is<object?[]>(a => a[0] != null && a[0]!.ToString() == "session-abc"),
            default), Times.Once);

        _chatNotifier.Verify(n => n.NotifyChatResponse("session-abc", It.IsAny<IReadOnlyList<string>>()), Times.Once);
    }

    [Fact]
    public async Task ReportChatResponse_WrongSession_ThrowsHubException()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeChatSessionId: "session-other");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);

        var message = new ChatResponseMessage
        {
            SessionId = "session-abc",
            Lines = ["line1"],
        };

        var hub = CreateHub();
        var act = () => hub.ReportChatResponse(message);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*session-abc*");
    }

    [Fact]
    public async Task ReportChatResponse_UnknownAgent_ThrowsHubException()
    {
        _facade.Setup(f => f.GetByConnectionId(It.IsAny<string>())).Returns((AgentEntry?)null);

        var message = new ChatResponseMessage
        {
            SessionId = "session-abc",
            Lines = ["line1"],
        };

        var hub = CreateHub();
        var act = () => hub.ReportChatResponse(message);

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task ReportChatCompleted_AgentOwnsSession_ClearsSessionAndBroadcasts()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeChatSessionId: "session-abc");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);

        _uiClientProxy
            .Setup(p => p.SendCoreAsync(HubMethodNames.OnChatCompleted, It.IsAny<object?[]>(), default))
            .Returns(Task.CompletedTask);

        var message = new ChatCompletedMessage
        {
            SessionId = "session-abc",
            ExitCode = 0,
        };

        var hub = CreateHub();
        await hub.ReportChatCompleted(message);

        // ActiveChatSessionId must be cleared
        agent.ActiveChatSessionId.Should().BeNull();

        _uiClients.Verify(c => c.Group("chat-session-session-abc"), Times.Once);
        _chatNotifier.Verify(n => n.NotifyChatCompleted("session-abc", 0, null), Times.Once);
    }

    [Fact]
    public async Task ReportChatCompleted_WrongSession_ThrowsHubException()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeChatSessionId: "session-other");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);

        var message = new ChatCompletedMessage
        {
            SessionId = "session-abc",
            ExitCode = 0,
        };

        var hub = CreateHub();
        var act = () => hub.ReportChatCompleted(message);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*session-abc*");
    }

    [Fact]
    public async Task ReportChatCompleted_UnknownAgent_ThrowsHubException()
    {
        _facade.Setup(f => f.GetByConnectionId(It.IsAny<string>())).Returns((AgentEntry?)null);

        var message = new ChatCompletedMessage
        {
            SessionId = "session-abc",
            ExitCode = 0,
        };

        var hub = CreateHub();
        var act = () => hub.ReportChatCompleted(message);

        await act.Should().ThrowAsync<HubException>();
    }

    // ── AgentHub.Consolidation ─────────────────────────────────────────

    [Fact]
    public async Task ReportFetchModelsResult_CompletesRequest()
    {
        // Arrange: pre-register a pending request so CompleteRequest finds it
        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid().ToString();

        // Use reflection to inject the pending TCS into ModelFetchService._pending
        var pendingField = typeof(ModelFetchService)
            .GetField("_pending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>>)pendingField.GetValue(_modelFetchService)!;
        pending[requestId] = tcs;

        var response = new FetchModelsResponse
        {
            RequestId = requestId,
            Models = [],
        };

        var hub = CreateHub();
        await hub.ReportFetchModelsResult(response);

        // The TCS should have been completed (removed from _pending)
        pending.Should().NotContainKey(requestId);
        tcs.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task ReportConsolidationComplete_ValidAgent_TransitionsToIdle()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-42");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);
        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "job-42",
            Success = true,
            Summary = "all good",
        };

        var hub = CreateHub();
        await hub.ReportConsolidationComplete(result);

        // Agent should be transitioned to Idle and ActiveJobId cleared
        agent.ActiveJobId.Should().BeNull();
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), AgentStatus.Idle), Times.Once);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
        _consolidationService.Verify(
            s => s.UpdateRunAsync(It.Is<RunId>(r => r.Value == "job-42"), ConsolidationRunStatus.Succeeded, "all good", It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Once);
    }

    [Fact]
    public async Task ReportConsolidationComplete_AgentNotFound_StillUpdatesRun()
    {
        _facade.Setup(f => f.GetByConnectionId(It.IsAny<string>())).Returns((AgentEntry?)null);
        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "job-99",
            Success = false,
            ErrorMessage = "agent crashed",
        };

        var hub = CreateHub();
        await hub.ReportConsolidationComplete(result);

        // No agent → no transition, but UpdateRunAsync must still be called
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        _consolidationService.Verify(
            s => s.UpdateRunAsync(It.Is<RunId>(r => r.Value == "job-99"), ConsolidationRunStatus.Failed, "agent crashed", It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Once);
    }

    [Fact]
    public async Task ReportConsolidationComplete_WithHarnessSuggestions_SavesAndIncrementsBadge()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-55");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);
        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);
        _consolidationService
            .Setup(s => s.SaveHarnessSuggestionsAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 10,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.8m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Frequency = 3, Rationale = "reason A", Text = "do A" },
                new() { Frequency = 2, Rationale = "reason B", Text = "do B" },
            },
        };

        var result = new ConsolidationJobResult
        {
            JobId = "job-55",
            Success = true,
            HarnessSuggestions = suggestions,
        };

        var initialBadge = _badgeService.BadgeCount;
        var hub = CreateHub();
        await hub.ReportConsolidationComplete(result);

        _consolidationService.Verify(s => s.SaveHarnessSuggestionsAsync(suggestions, It.IsAny<CancellationToken>()), Times.Once);
        _badgeService.BadgeCount.Should().Be(initialBadge + 2);
    }

    [Fact]
    public async Task ReportConsolidationComplete_WithCreatedIssues_IncrementsBadge()
    {
        var agent = CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-66");
        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(agent);
        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var result = new ConsolidationJobResult
        {
            JobId = "job-66",
            Success = true,
            CreatedIssues = new List<CreatedIssueInfo>
            {
                new() { Identifier = "org/repo#10", Title = "Issue A", Url = "https://example.com/10" },
                new() { Identifier = "org/repo#11", Title = "Issue B", Url = "https://example.com/11" },
                new() { Identifier = "org/repo#12", Title = "Issue C", Url = "https://example.com/12" },
            },
        };

        var initialBadge = _badgeService.BadgeCount;
        var hub = CreateHub();
        await hub.ReportConsolidationComplete(result);

        _badgeService.BadgeCount.Should().Be(initialBadge + 3);
    }

    // ── AgentHub.IssueOps ─────────────────────────────────────────────

    private static PipelineRun CreateRun(string runId, string agentId = "agent-1") => new()
    {
        RunId = runId,
        AgentId = agentId,
        IssueIdentifier = "owner/repo#5",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "provider-1",
        RepoProviderConfigId = "repo-provider-1",
    };

    [Fact]
    public async Task RequestLabelChange_ValidLabel_SwapsLabel()
    {
        var run = CreateRun("job-10");
        var jobId = new JobId { Value = "job-10" };

        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(
            CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-10"));
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-10"))).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.InProgress)).Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.RequestLabelChange(jobId, AgentLabels.InProgress);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.InProgress), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_UnknownRun_DoesNotSwapLabel()
    {
        var jobId = new JobId { Value = "job-unknown" };

        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(
            CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-unknown"));
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-unknown"))).Returns((PipelineRun?)null);

        var hub = CreateHub();
        await hub.RequestLabelChange(jobId, AgentLabels.InProgress);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RequestLabelChange_InvalidLabel_IsIgnored()
    {
        var run = CreateRun("job-10");
        var jobId = new JobId { Value = "job-10" };

        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(
            CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-10"));
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-10"))).Returns(run);

        var hub = CreateHub();
        await hub.RequestLabelChange(jobId, "not-a-real-label");

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RequestLabelChange_GatedLabel_IsIgnored()
    {
        var run = CreateRun("job-10");
        var jobId = new JobId { Value = "job-10" };

        _facade.Setup(f => f.GetByConnectionId("agent-conn-1")).Returns(
            CreateAgentEntry("agent-1", "agent-conn-1", activeJobId: "job-10"));
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-10"))).Returns(run);

        // AgentLabels.EpicApproved is in DispatchGatedLabels — agents cannot set it
        var hub = CreateHub();
        await hub.RequestLabelChange(jobId, AgentLabels.EpicApproved);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }
}
