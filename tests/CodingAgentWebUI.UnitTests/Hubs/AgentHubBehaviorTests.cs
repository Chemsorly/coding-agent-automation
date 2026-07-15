using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>Unit tests for <see cref="AgentHub"/> behavior (method logic, not models).</summary>
public sealed class AgentHubBehaviorTests : IDisposable
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly Mock<IHubIssueOperations> _mockIssueOps = new();
    private readonly Mock<IAgentJobLifecycleService> _mockLifecycleService = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly List<PipelineOrchestrationService> _orchestrationInstances = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(
            _mockFacade.Object,
            _mockTokenVending.Object,
            null!,  // PipelineOrchestrationService — tests that need it use a mock below
            null!,  // ModelFetchService
            _mockConsolidation.Object,
            _badgeService,
            _mockIssueOps.Object,
            _mockLifecycleService.Object,
            _mockLogger.Object);

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private static AgentEntry CreateAgent(string agentId = "agent-1", string connectionId = "conn-1") => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static PipelineRun CreateRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    #region ReportJobCompleted

    [Fact]
    public async Task ReportJobCompleted_UpdatesRunFields()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var now = DateTimeOffset.UtcNow;
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = now,
            PullRequestUrl = "https://github.com/org/repo/pull/1",
            PullRequestNumber = "1",
            RetryCount = 2,
            FilesChangedCount = 5,
            LinesAdded = 100,
            LinesRemoved = 20
        };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        // Use a hub with a real PipelineOrchestrationService mock to avoid NRE on NotifyChange
        var hub = CreateHubWithOrchestration();

        await hub.ReportJobCompleted("job-1", payload);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CompletedAt.Should().Be(now.UtcDateTime);
        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/1");
        run.RetryCount.Should().Be(2);
        run.FilesChangedCount.Should().Be(5);
        run.LinesAdded.Should().Be(100);
        run.LinesRemoved.Should().Be(20);
    }

    [Fact]
    public async Task ReportJobCompleted_AddsToHistory_RemovesRun()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        // History + removal now delegated to lifecycle manager's CompleteRunAsync
        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_FailedStep_SwapsToErrorLabel()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_CompletedStep_SwapsToDoneLabel()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", AgentLabels.Done, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_FinalLabel_OverridesFinalStepInference()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.NeedsRefinement
        };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", AgentLabels.NeedsRefinement, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_FinalLabelWontDo_OverridesCompletedStep()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.WontDo
        };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", AgentLabels.WontDo, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_TransitionsAgentToIdle_DoesNotSignalDrain()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        // Signal is NOT called — agent sends AgentReady after clearing its local slot
        _mockFacade.Verify(f => f.Signal(), Times.Never);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReportJobCompleted_NullRun_StillTransitionsAgent()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        // Signal is NOT called — agent sends AgentReady after clearing its local slot
        _mockFacade.Verify(f => f.Signal(), Times.Never);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReportJobCompleted_RunAlreadyRemoved_NoStateChange_NoDoublePersist()
    {
        // Simulates a late ReportJobCompleted arriving after CancelRunAsync already removed the run.
        // In production, the [RequiresActiveJob] filter would reject this call first (Layer 1 defense).
        // This test validates the secondary defense (Layer 2): even if the message reaches the hub
        // method body, CompleteRunAsync is never called and TransitionWorkItemAsync is the only action.
        // TODO: This test doesn't truly validate "NoDoublePersist" — a proper test would: (1) cancel a run
        // via CancelRunAsync, (2) send a late ReportJobCompleted, and (3) verify AddRunToHistoryAsync is NOT
        // called a second time. Currently it only proves CompleteRunAsync isn't called when run is missing.
        // TODO: Assert agent.ActiveJobId becomes null and TransitionStatus(AgentStatus.Idle) is called after
        // the call to validate complete agent state cleanup in the late-arrival path (the hub always clears
        // ActiveJobId and transitions to Idle regardless of run existence).
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null); // Run already removed by CancelRunAsync

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        // CompleteRunAsync never called (run not in memory)
        _mockLifecycleManager.Verify(l => l.CompleteRunAsync(
            It.IsAny<string>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Never);

        // Recovery path: TransitionWorkItemAsync called (will no-op at DB level for terminal state)
        _mockFacade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    #endregion

    #region ReportJobCompleted_WorkItemTransition

    [Fact]
    public async Task ReportJobCompleted_Succeeded_TransitionsWorkItemToSucceeded()
    {
        // This test asserts that when an agent reports job completion,
        // the lifecycle manager is called with the correct terminal status.
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    [Fact]
    public async Task ReportJobCompleted_Failed_TransitionsWorkItemToFailed()
    {
        var agent = CreateAgent();
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        // TODO: Use specific matchers for errorMsg and failureReason instead of It.IsAny<>().
        // Current test would not detect a regression where error message or failure reason is
        // accidentally passed as null or with incorrect values for the Failed path.
        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
    }

    #endregion

    #region JobRejected

    [Fact]
    public async Task JobRejected_ResetsAgentToIdle_SignalsDrain()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        await hub.JobRejected("job-1", "workspace full");

        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        _mockFacade.Verify(f => f.Signal(), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    #endregion

    #region RequestLabelChange

    [Fact]
    public async Task RequestLabelChange_ValidRun_DelegatesToLabelSwapper()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.RequestLabelChange("job-1", "agent:error");

        _mockIssueOps.Verify(s => s.SwapLabelAsync(run, "agent:error", LabelTargetKind.Issue), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_UnknownRun_ReturnsEarly()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        await hub.RequestLabelChange("job-1", "agent:error");

        _mockIssueOps.Verify(s => s.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>()), Times.Never);
    }

    #endregion

    #region RequestPostComment

    // TODO: This test was weakened during lifecycle extraction — it only verifies delegation to IHubIssueOperations mock,
    // not the full interaction with IssueProvider (PostCommentAsync with correct args). Consider adding an integration-level
    // test that exercises AgentIssueOperations.PostCommentViaIssueProviderAsync through the hub's RequestPostComment path.
    [Fact]
    public async Task RequestPostComment_AnalysisType_PostsMarkdown()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "## Analysis\nLooks good." };
        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "## Analysis\nLooks good."), Times.Once);
    }

    [Fact]
    public async Task RequestPostComment_UnknownRun_ReturnsEarly()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "test" };
        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        _mockFacade.Verify(f => f.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestPostComment_UnknownCommentType_ReturnsEarly()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "test" };
        await hub.RequestPostComment("job-1", (CommentType)99, payload);

        _mockFacade.Verify(f => f.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region ReportConsolidationComplete

    [Fact]
    public async Task ReportConsolidationComplete_Success_UpdatesRunStatus()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "Done" };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s => s.UpdateRunAsync("crun-1", ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        _mockFacade.Verify(f => f.Signal(), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReportConsolidationComplete_Failed_UpdatesRunAsFailed()
    {
        var agent = CreateAgent();
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = false, ErrorMessage = "Timeout" };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s => s.UpdateRunAsync("crun-1", ConsolidationRunStatus.Failed, "Timeout", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ReportConsolidationComplete_WithSuggestions_PersistsAndIncrementsBadge()
    {
        var agent = CreateAgent();
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var suggestions = new HarnessSuggestions
        {
            GeneratedAtUtc = DateTime.UtcNow,
            BasedOnRunCount = 10,
            SuccessRate = 0.8m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Text = "S1", Rationale = "R1", Frequency = 5 },
                new() { Text = "S2", Rationale = "R2", Frequency = 3 }
            }
        };

        var hub = CreateHubWithOrchestration();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "OK", HarnessSuggestions = suggestions };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s => s.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None), Times.Once);
        _badgeService.BadgeCount.Should().Be(2);
    }

    [Fact]
    public async Task ReportConsolidationComplete_WithCreatedIssues_IncrementsBadge()
    {
        var agent = CreateAgent();
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-1",
            Success = true,
            Summary = "OK",
            CreatedIssues = new List<CreatedIssueInfo>
            {
                new() { Identifier = "#1", Title = "Issue 1", Url = "http://x" },
                new() { Identifier = "#2", Title = "Issue 2", Url = "http://y" }
            }
        };

        await hub.ReportConsolidationComplete(result);

        _badgeService.BadgeCount.Should().Be(2);
    }

    #endregion

    #region RequestTokenRefresh — K8s Mode Fallback

    [Fact]
    public async Task RequestTokenRefresh_WithPipelineRun_UsesRunRepoConfigId()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var repoConfig = new ProviderConfig
        {
            Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string> { ["privateKeyBase64"] = "key123", ["clientId"] = "c", ["installationId"] = "1" }
        };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(repoConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("fresh-token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();
        var result = await hub.RequestTokenRefresh("job-1", ProviderKind.Repository);

        result.Token.Should().Be("fresh-token");
        _mockTokenVending.Verify(t => t.GenerateAgentTokenAsync(repoConfig, It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Fact]
    public async Task RequestTokenRefresh_NoPipelineRun_FallsBackToWorkItemPayload()
    {
        // K8s mode: no PipelineRun exists — falls back to WorkItem payload lookup
        _mockFacade.Setup(f => f.GetRun("wi-k8s-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-from-payload", "brain-from-payload"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-from-payload", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string> { ["privateKeyBase64"] = "key", ["clientId"] = "c", ["installationId"] = "1" }
        };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-from-payload", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(repoConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("k8s-token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();
        var result = await hub.RequestTokenRefresh("wi-k8s-1", ProviderKind.Repository);

        result.Token.Should().Be("k8s-token");
        _mockFacade.Verify(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestTokenRefresh_NoPipelineRunAndNoWorkItem_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetRun("nonexistent")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?, string?)?)null);

        var hub = CreateHub();
        var act = () => hub.RequestTokenRefresh("nonexistent", ProviderKind.Repository);

        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run or work item*");
    }

    [Fact]
    public async Task RequestTokenRefresh_K8sFallback_BrainKind_UsesBrainConfigId()
    {
        _mockFacade.Setup(f => f.GetRun("wi-brain-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-brain-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-cfg", "brain-cfg"));

        var brainConfig = new ProviderConfig
        {
            Id = "brain-cfg", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain",
            Settings = new Dictionary<string, string> { ["privateKeyBase64"] = "brainkey", ["clientId"] = "c", ["installationId"] = "1" }
        };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-cfg", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(brainConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("brain-token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();
        var result = await hub.RequestTokenRefresh("wi-brain-1", ProviderKind.Brain);

        result.Token.Should().Be("brain-token");
        _mockFacade.Verify(f => f.GetProviderConfigByIdAsync("brain-cfg", ProviderKind.Repository, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region RegisterAgent — AlreadyTracked path sets agent to Busy

    [Fact]
    public async Task RegisterAgent_AlreadyTrackedRun_SetsAgentBusyAndActiveJobId()
    {
        // Reproduce: K8s dispatch creates PipelineRun with AgentId=null.
        // Agent pod registers with ActiveJob matching that run.
        // Bug: "already tracked" branch did nothing → agent stays Idle.
        // Fix: must set run.AgentId, entry.ActiveJobId, and transition to Busy.
        const string agentId = "caa-23da8a6e-4cblw";
        const string runId = "23da8a6e-89fb-422a-9115-4f3ea173af35";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#100",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = null // Dispatched without agent assignment (K8s mode)
        };

        var agentEntry = new AgentEntry
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            Status = AgentStatus.Idle,
            ActiveJobId = null,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        // Facade returns the existing run and agent entry
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(agentEntry);
        _mockFacade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(agentEntry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // Setup context to pass validation (query param + user claim)
        var hub = CreateHubWithRegistrationContext(agentId, "conn-1");

        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            ActiveJob = new ActiveJobState
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#100",
                IssueTitle = "Test Issue",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                AgentProviderConfigId = "ap-1",
                InitiatedBy = "loop",
                CurrentStep = PipelineStep.AnalyzingCode,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        };

        // Act
        await hub.RegisterAgent(message);

        // Assert: agent must be transitioned to Busy with ActiveJobId set
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        agentEntry.ActiveJobId.Should().Be(runId);
        // Run's AgentId should be updated
        existingRun.AgentId.Should().Be(agentId);
    }

    #endregion

    #region RegisterAgent — RunInHistory path (cancelled/failed/completed FinalStep)

    [Fact]
    public async Task RegisterAgent_RunInHistory_CancelledFinalStep_RestoresRunAndSetsAgentBusy()
    {
        // Scenario: WorkItem was cancelled (persisted to history with FinalStep=Cancelled),
        // then re-dispatched with the same RunId. Agent registers reporting that RunId.
        // Expected: the Cancelled history entry should NOT block restoration.
        const string agentId = "caa-49fba05c-f2vhg";
        const string runId = "49fba05c-3210-4a2a-93be-0f8ea7f7cb79";

        var agentEntry = new AgentEntry
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            Status = AgentStatus.Idle,
            ActiveJobId = null,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        // GetRun returns null — no in-memory run (K8s mode, no rehydration after cancel)
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(agentEntry);
        _mockFacade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(agentEntry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        // History contains the run with FinalStep = Cancelled
        var cancelledSummary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#1259",
            IssueTitle = "Fix revert using cancelled token",
            FinalStep = PipelineStep.Cancelled,
            StartedAt = DateTime.UtcNow.AddHours(-8),
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-8)
        };
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cancelledSummary });

        var hub = CreateHubWithRegistrationContext(agentId, "conn-1");

        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            ActiveJob = new ActiveJobState
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#1259",
                IssueTitle = "Fix revert using cancelled token",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                AgentProviderConfigId = "ap-1",
                InitiatedBy = "loop",
                CurrentStep = PipelineStep.AnalyzingCode,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        };

        // Act
        await hub.RegisterAgent(message);

        // Assert: run must be restored via AddRun
        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.AgentId == agentId &&
            r.IssueIdentifier == "owner/repo#1259" &&
            r.CurrentStep == PipelineStep.AnalyzingCode)), Times.Once);

        // Assert: agent transitioned to Busy with ActiveJobId
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        agentEntry.ActiveJobId.Should().Be(runId);
    }

    [Fact]
    public async Task RegisterAgent_RunInHistory_FailedFinalStep_RestoresRunAndSetsAgentBusy()
    {
        // Scenario: WorkItem failed (persisted to history with FinalStep=Failed),
        // then re-dispatched with the same RunId. Agent registers reporting that RunId.
        // Expected: the Failed history entry should NOT block restoration.
        const string agentId = "caa-failed-agent";
        const string runId = "failed-run-id-1234";

        var agentEntry = new AgentEntry
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            Status = AgentStatus.Idle,
            ActiveJobId = null,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(agentEntry);
        _mockFacade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(agentEntry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        // History contains the run with FinalStep = Failed
        var failedSummary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#999",
            IssueTitle = "Previously failed issue",
            FinalStep = PipelineStep.Failed,
            StartedAt = DateTime.UtcNow.AddHours(-4),
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-4)
        };
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { failedSummary });

        var hub = CreateHubWithRegistrationContext(agentId, "conn-1");

        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            ActiveJob = new ActiveJobState
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#999",
                IssueTitle = "Previously failed issue",
                IssueProviderConfigId = "ip-2",
                RepoProviderConfigId = "rp-2",
                AgentProviderConfigId = "ap-2",
                InitiatedBy = "loop",
                CurrentStep = PipelineStep.GeneratingCode,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
            }
        };

        // Act
        await hub.RegisterAgent(message);

        // Assert: run must be restored via AddRun
        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.AgentId == agentId &&
            r.IssueIdentifier == "owner/repo#999" &&
            r.CurrentStep == PipelineStep.GeneratingCode)), Times.Once);

        // Assert: agent transitioned to Busy with ActiveJobId
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        agentEntry.ActiveJobId.Should().Be(runId);
    }

    [Fact]
    public async Task RegisterAgent_RunInHistory_CompletedFinalStep_IgnoresStaleState()
    {
        // Scenario: Run completed successfully (FinalStep=Completed in history).
        // Agent registers reporting the same RunId (stale state from old pod).
        // Expected: agent remains Idle, no run restoration.
        const string agentId = "caa-stale-agent";
        const string runId = "completed-run-id-5678";

        var agentEntry = new AgentEntry
        {
            AgentId = agentId,
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            Status = AgentStatus.Idle,
            ActiveJobId = null,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(agentEntry);
        _mockFacade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(agentEntry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        // History contains the run with FinalStep = Completed (successful)
        var completedSummary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#500",
            IssueTitle = "Successfully completed issue",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-2)
        };
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { completedSummary });

        var hub = CreateHubWithRegistrationContext(agentId, "conn-1");

        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "k8s-pod",
            Labels = new[] { "kiro", "dotnet" },
            ActiveJob = new ActiveJobState
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#500",
                IssueTitle = "Successfully completed issue",
                IssueProviderConfigId = "ip-3",
                RepoProviderConfigId = "rp-3",
                AgentProviderConfigId = "ap-3",
                InitiatedBy = "loop",
                CurrentStep = PipelineStep.GeneratingCode,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            }
        };

        // Act
        await hub.RegisterAgent(message);

        // Assert: AddRun must NOT be called (stale state rejected)
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);

        // Assert: agent must NOT be transitioned to Busy
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);

        // Assert: ActiveJobId remains null
        agentEntry.ActiveJobId.Should().BeNull();
    }

    #endregion

    #region Helpers

    private AgentHub CreateHubWithOrchestration(string connectionId = "conn-1")
    {
        var orchestration = CreateMinimalOrchestrationService();

        var hub = new AgentHub(
            _mockFacade.Object,
            _mockTokenVending.Object,
            orchestration,
            null!,  // ModelFetchService
            _mockConsolidation.Object,
            _badgeService,
            _mockIssueOps.Object,
            CreateRealLifecycleService(orchestration),
            _mockLogger.Object);

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private AgentHub CreateHubWithRegistrationContext(string agentId, string connectionId)
    {
        var orchestration = CreateMinimalOrchestrationService();

        var hub = new AgentHub(
            _mockFacade.Object,
            _mockTokenVending.Object,
            orchestration,
            null!,  // ModelFetchService
            _mockConsolidation.Object,
            _badgeService,
            _mockIssueOps.Object,
            CreateRealLifecycleService(orchestration),
            _mockLogger.Object);

        // Build a real HttpContext with the agentId query param
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?agentId={agentId}");

        // GetHttpContext() extension reads from IHttpContextFeature in Features
        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
            new TestHttpContextFeature(httpContext));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        mockContext.Setup(c => c.Features).Returns(features);
        // No authenticated user claim (skips defense-in-depth check)
        mockContext.Setup(c => c.User).Returns((System.Security.Claims.ClaimsPrincipal?)null);
        hub.Context = mockContext.Object;

        return hub;
    }

    private sealed class TestHttpContextFeature : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public TestHttpContextFeature(Microsoft.AspNetCore.Http.HttpContext httpContext) => HttpContext = httpContext;
        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
    }

    private PipelineOrchestrationService CreateMinimalOrchestrationService()
    {
        var service = TestOrchestrationFactory.CreateMinimal(
            configStore: Mock.Of<IConfigurationStore>(),
            providerFactory: Mock.Of<IProviderFactory>());
        _orchestrationInstances.Add(service);
        return service;
    }

    private IAgentJobLifecycleService CreateRealLifecycleService(PipelineOrchestrationService orchestration)
    {
        var issueOps = new AgentIssueOperations(
            _mockFacade.Object,
            _mockLabelSwapper.Object,
            _mockLogger.Object);
        return new AgentJobLifecycleService(
            _mockFacade.Object,
            _mockLifecycleManager.Object,
            _mockLabelSwapper.Object,
            issueOps,
            orchestration,
            _mockLogger.Object);
    }

    [Fact]
    public async Task ReportJobCompleted_WhenCompleteRunAsyncThrows_StillCleansDedupGuardAndActiveRun()
    {
        // Arrange: CompleteRunAsync throws (simulating DB failure during completion)
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("Simulated DB failure"));

        var hub = CreateHubWithOrchestration();

        // Act: should not throw despite CompleteRunAsync failure
        await hub.ReportJobCompleted("job-1", payload);

        // Assert: defensive cleanup must release the dedup guard and remove the orphaned run
        _mockFacade.Verify(f => f.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId), Times.Once);
        _mockFacade.Verify(f => f.RemoveRun("job-1"), Times.Once);
    }

    #endregion

    #region ReportStepTransition

    [Fact]
    public async Task ReportStepTransition_UpdatesCurrentStepAndHighWaterMark()
    {
        var run = CreateRun();
        run.CurrentStep = PipelineStep.Created;
        run.HighWaterMark = PipelineStep.Created;
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesBranchName()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string> { ["BranchName"] = "feature/test-123" };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.VerifyingBaseline, DateTimeOffset.UtcNow, metadata);

        run.BranchName.Should().Be("feature/test-123");
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesBaselineHealthPassed()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string> { ["BaselineHealthPassed"] = "True" };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow, metadata);

        run.BaselineHealthPassed.Should().BeTrue();
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesFileChangeStats()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["FilesChangedCount"] = "7",
            ["LinesAdded"] = "120",
            ["LinesRemoved"] = "30"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.ReviewingCode, DateTimeOffset.UtcNow, metadata);

        run.FilesChangedCount.Should().Be(7);
        run.LinesAdded.Should().Be(120);
        run.LinesRemoved.Should().Be(30);
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesCodeReviewProgress()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["CodeReviewIterationsCompleted"] = "2",
            ["CodeReviewIterationsTotal"] = "3"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow, metadata);

        run.CodeReviewIterationsCompleted.Should().Be(2);
        run.CodeReviewIterationsTotal.Should().Be(3);
    }

    [Fact]
    public async Task ReportStepTransition_NullMetadata_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, null);

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task ReportStepTransition_EmptyMetadata_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, new Dictionary<string, string>());

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task ReportStepTransition_UnknownMetadataKeys_AreIgnored()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["UnknownKey"] = "some-value",
            ["AnotherFakeKey"] = "123"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, metadata);

        // Run state should be unchanged (only CurrentStep updated by the transition itself)
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        run.BranchName.Should().BeNull();
        run.BaselineHealthPassed.Should().BeNull();
        run.FilesChangedCount.Should().Be(0);
    }

    [Fact]
    public async Task ReportStepTransition_MalformedBooleanValue_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string> { ["BaselineHealthPassed"] = "not-a-bool" };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow, metadata);

        // Malformed bool should result in null (TryParse fails)
        run.BaselineHealthPassed.Should().BeNull();
    }

    [Fact]
    public async Task ReportStepTransition_MalformedIntegerValue_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["FilesChangedCount"] = "abc",
            ["LinesAdded"] = "",
            ["LinesRemoved"] = "not-a-number"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.ReviewingCode, DateTimeOffset.UtcNow, metadata);

        // Malformed ints should leave values at default (0)
        run.FilesChangedCount.Should().Be(0);
        run.LinesAdded.Should().Be(0);
        run.LinesRemoved.Should().Be(0);
    }

    [Fact]
    public async Task ReportStepTransition_NullRun_DoesNotThrow()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string> { ["BranchName"] = "feature/test" };
        var hub = CreateHubWithOrchestration();

        // Should not throw even when run is not found
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, metadata);
    }

    [Fact]
    public async Task ReportStepTransition_HighWaterMark_DoesNotGoBackward()
    {
        var run = CreateRun();
        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.HighWaterMark = PipelineStep.RunningQualityGates;
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var hub = CreateHubWithOrchestration();
        // Transition backward (retry scenario)
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);

        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        run.HighWaterMark.Should().Be(PipelineStep.RunningQualityGates); // Should NOT go backward
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesRetryCount()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["RetryCount"] = "2",
            ["InfrastructureRetryCount"] = "1"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, metadata);

        run.RetryCount.Should().Be(2);
        run.InfrastructureRetryCount.Should().Be(1);
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesTotalTokensAndCost()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["TotalTokens"] = "150000",
            ["TotalCost"] = "4.56"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow, metadata);

        run.TotalTokens.Should().Be(150000);
        run.TotalCost.Should().Be(4.56m);
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_AppliesCodeReviewCounts()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var metadata = new Dictionary<string, string>
        {
            ["CodeReviewCriticalCount"] = "3",
            ["CodeReviewWarningCount"] = "5",
            ["CodeReviewSuggestionCount"] = "7",
            ["CodeReviewAgentsRun"] = "security-agent\x1Fstyle-agent"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow, metadata);

        run.CodeReviewCriticalCount.Should().Be(3);
        run.CodeReviewWarningCount.Should().Be(5);
        run.CodeReviewSuggestionCount.Should().Be(7);
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "security-agent", "style-agent" });
    }

    [Fact]
    public async Task ReportStepTransition_WithMetadata_CodeReviewCounts_AppliedAtomically()
    {
        var run = CreateRun();
        // Pre-set some values to verify they get replaced, not added to
        run.SetCodeReviewCounts(10, 20, 30);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        // Only send Critical — Warning and Suggestion should keep existing values
        var metadata = new Dictionary<string, string>
        {
            ["CodeReviewCriticalCount"] = "5"
        };
        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow, metadata);

        run.CodeReviewCriticalCount.Should().Be(5);
        run.CodeReviewWarningCount.Should().Be(20); // Preserved
        run.CodeReviewSuggestionCount.Should().Be(30); // Preserved
    }

    // TODO: Add a complementary test supplying a past timestamp and asserting LastStepChangeAt equals that exact value,
    // to validate the pass-through path and catch accidental unconditional UtcNow assignment.
    [Fact]
    public async Task ReportStepTransition_FarFutureTimestamp_IsClampedToUtcNow()
    {
        var run = CreateRun();
        run.CurrentStep = PipelineStep.Created;
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(CreateAgent());

        var hub = CreateHubWithOrchestration();
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow.AddHours(24));

        run.LastStepChangeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region ReportChatResponse / ReportChatCompleted — Session Ownership

    [Fact]
    public async Task ReportChatResponse_AgentOwnsSession_Succeeds()
    {
        var agent = CreateAgent();
        agent.ActiveChatSessionId = "s1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var message = new ChatResponseMessage { SessionId = "s1", Lines = new[] { "hello" } };

        await hub.ReportChatResponse(message);
        // No exception — success
    }

    [Fact]
    public async Task ReportChatResponse_AgentDoesNotOwnSession_ThrowsHubException()
    {
        var agent = CreateAgent();
        agent.ActiveChatSessionId = "s1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatResponseMessage { SessionId = "s2", Lines = new[] { "hello" } };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().ThrowAsync<HubException>().WithMessage("*not assigned*");
    }

    [Fact]
    public async Task ReportChatResponse_UnknownConnection_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var message = new ChatResponseMessage { SessionId = "s1", Lines = new[] { "hello" } };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().ThrowAsync<HubException>().WithMessage("*not assigned*");
    }

    [Fact]
    public async Task ReportChatCompleted_AgentOwnsSession_Succeeds()
    {
        var agent = CreateAgent();
        agent.ActiveChatSessionId = "s1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var message = new ChatCompletedMessage { SessionId = "s1", ExitCode = 0 };

        await hub.ReportChatCompleted(message);
        // No exception — success
    }

    [Fact]
    public async Task ReportChatCompleted_AgentDoesNotOwnSession_ThrowsHubException()
    {
        var agent = CreateAgent();
        agent.ActiveChatSessionId = "s1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatCompletedMessage { SessionId = "s2", ExitCode = 0 };

        var act = () => hub.ReportChatCompleted(message);
        await act.Should().ThrowAsync<HubException>().WithMessage("*not assigned*");
    }

    [Fact]
    public async Task ReportChatCompleted_ClearsActiveChatSessionId()
    {
        var agent = CreateAgent();
        agent.ActiveChatSessionId = "s1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHubWithOrchestration();
        var message = new ChatCompletedMessage { SessionId = "s1", ExitCode = 0 };

        await hub.ReportChatCompleted(message);

        agent.ActiveChatSessionId.Should().BeNull();
    }

    #endregion

    #region Heartbeat — Progress Refresh

    [Fact]
    public async Task Heartbeat_ActiveStepMatchesRun_RefreshesLastStepChangeAt()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-50); // Nearly timed out

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetByAgentId("agent-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.RunningQualityGates,
            MemoryUsageMb = 512
        };

        await hub.Heartbeat(heartbeat);

        // LastStepChangeAt should be refreshed (close to now, not -50min)
        run.LastStepChangeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Heartbeat_NullCurrentStep_DoesNotRefreshLastStepChangeAt()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        run.CurrentStep = PipelineStep.RunningQualityGates;
        var originalTimestamp = DateTimeOffset.UtcNow.AddMinutes(-50);
        run.LastStepChangeAt = originalTimestamp;

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetByAgentId("agent-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = null, // Agent considers itself idle
            MemoryUsageMb = 512
        };

        await hub.Heartbeat(heartbeat);

        // LastStepChangeAt should NOT be refreshed — preserves stuck-agent detection
        run.LastStepChangeAt.Should().Be(originalTimestamp);
    }

    [Fact]
    public async Task Heartbeat_StepMismatch_DoesNotRefreshLastStepChangeAt()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        run.CurrentStep = PipelineStep.RunningQualityGates;
        var originalTimestamp = DateTimeOffset.UtcNow.AddMinutes(-50);
        run.LastStepChangeAt = originalTimestamp;

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetByAgentId("agent-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.GeneratingCode, // Doesn't match run's current step
            MemoryUsageMb = 512
        };

        await hub.Heartbeat(heartbeat);

        // LastStepChangeAt should NOT be refreshed — step mismatch
        run.LastStepChangeAt.Should().Be(originalTimestamp);
    }

    [Fact]
    public async Task Heartbeat_FutureTimestamp_ClampedToServerTime()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.LastStepChangeAt = DateTimeOffset.UtcNow.AddMinutes(-50);

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetByAgentId("agent-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = DateTimeOffset.UtcNow.AddHours(5), // Far-future timestamp
            CurrentStep = PipelineStep.RunningQualityGates,
            MemoryUsageMb = 512
        };

        await hub.Heartbeat(heartbeat);

        // Should be clamped to approximately now, not the future timestamp
        run.LastStepChangeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Heartbeat_NoActiveJob_DoesNotThrow()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = null; // No active job

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetByAgentId("agent-1")).Returns(agent);

        var hub = CreateHub();
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.RunningQualityGates,
            MemoryUsageMb = 512
        };

        // Should not throw
        await hub.Heartbeat(heartbeat);
    }

    #endregion

    #region PostIssueFeedbackComment — Feedback link in PR body

    [Fact]
    public async Task PostIssueFeedbackComment_WhenCommentUrlReturned_AppendsFeedbackLinkToPrBody()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";
        run.PullRequestBody = "Existing PR body";

        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        // Issue provider setup — returns a comment URL
        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.PostCommentAsync("org/repo#42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-999");
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockFacade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        // Repo provider setup — captures the updated body
        var repoConfig = new ProviderConfig { Id = "repo-cfg-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-cfg-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.GetPullRequestBodyAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Existing PR body");
        _mockFacade.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(mockRepoProvider.Object);

        var hub = CreateHubWithOrchestration();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestNumber = "99",
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback(),
                Issue = new IssueFeedback { Description = "AC contradicts stakeholder comment" }
            }
        };
        await hub.ReportJobCompleted("job-1", payload);

        // Assert — PR body was updated with feedback link
        mockRepoProvider.Verify(r => r.UpdatePullRequestAsync(99,
            It.Is<string>(body => body.Contains("## Agent Feedback") && body.Contains("issuecomment-999")),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostIssueFeedbackComment_WhenFeedbackAlreadyInBody_SkipsAppend()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";
        run.PullRequestBody = "Existing body\n\n## Agent Feedback\nAlready here.";

        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.PostCommentAsync("org/repo#42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/issues/42#issuecomment-999");
        mockIssueProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockFacade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        var hub = CreateHubWithOrchestration();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback(),
                Issue = new IssueFeedback { Description = "Some feedback" }
            }
        };
        await hub.ReportJobCompleted("job-1", payload);

        // Assert — repo provider never called (idempotency guard triggered)
        _mockFacade.Verify(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task PostIssueFeedbackComment_WhenNoFeedback_DoesNotPostOrAppend()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";
        run.Feedback = null;

        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
        await hub.ReportJobCompleted("job-1", payload);

        // Assert — no issue provider created (no comment to post)
        _mockFacade.Verify(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region JobRejected_GhostWorkItem

    [Fact]
    public async Task JobRejected_FirstRejection_RequeuesWorkItem_NotPermanentFailure()
    {
        // On first rejection (dispatch race), the work item should be re-queued
        // (transitioned back to Pending) so the drain service picks it up again.
        // Only after max retries (3) should it permanently fail with agent:error.
        var agent = CreateAgent();
        var run = CreateRun("job-requeue-1");
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-requeue-1")).Returns(run);
        // Simulate first rejection: facade reports RetryCount=0 for this work item
        _mockFacade.Setup(f => f.GetWorkItemRetryCountAsync("job-requeue-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var hub = CreateHubWithOrchestration();
        await hub.JobRejected("job-requeue-1", "Agent is busy");

        // Should re-queue (transition to Pending with incremented retry), NOT fail
        _mockFacade.Verify(f => f.RequeueWorkItemAsync("job-requeue-1", It.IsAny<CancellationToken>()), Times.Once);
        // Should NOT transition to Failed
        _mockFacade.Verify(f => f.TransitionWorkItemAsync("job-requeue-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Never);
        // Should NOT swap label to agent:error (item will be retried)
        _mockLabelSwapper.Verify(s => s.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), AgentLabels.Error, It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JobRejected_ThirdRejection_PermanentlyFails()
    {
        // After 3 rejections, the work item should be permanently marked as Failed
        // with agent:error label — human intervention needed.
        var agent = CreateAgent();
        var run = CreateRun("job-maxretry-1");
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-maxretry-1")).Returns(run);
        // Simulate third rejection: RetryCount already at 3
        _mockFacade.Setup(f => f.GetWorkItemRetryCountAsync("job-maxretry-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var hub = CreateHubWithOrchestration();
        await hub.JobRejected("job-maxretry-1", "Agent is busy");

        // Should permanently fail (not re-queue)
        _mockFacade.Verify(f => f.TransitionWorkItemAsync("job-maxretry-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>()), Times.Once);
        _mockFacade.Verify(f => f.RequeueWorkItemAsync("job-maxretry-1", It.IsAny<CancellationToken>()), Times.Never);
        // Should swap label to agent:error
        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JobRejected_Requeue_ClearsDedupEntry_SoDrainCanRedispatch()
    {
        // When re-queuing, MarkIssueComplete MUST be called to clear the dedup tracker.
        // Without this, the in-memory _processingIssues entry from the original dispatch
        // blocks re-dispatch attempts. The drain service works from DB state (Pending status),
        // but manual dispatch or loop re-poll would be blocked by the stale dedup entry.
        var agent = CreateAgent();
        var run = CreateRun("job-requeue-dedup");
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-requeue-dedup")).Returns(run);
        _mockFacade.Setup(f => f.GetWorkItemRetryCountAsync("job-requeue-dedup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var hub = CreateHubWithOrchestration();
        await hub.JobRejected("job-requeue-dedup", "Agent is busy");

        // MarkIssueComplete MUST be called to clear dedup tracker
        _mockFacade.Verify(f => f.MarkIssueComplete("org/repo#42", "issue-cfg-1"), Times.Once);
    }

    #endregion

    #region ReportJobCompleted_SignalRace

    [Fact]
    public async Task ReportJobCompleted_DoesNotSignalDrainService_ToPreventDispatchRace()
    {
        // Reproduction for dispatch race condition: ReportJobCompleted called Signal()
        // which woke the drain service before the agent cleared its local _activeJobId.
        // The drain dispatched to the agent which rejected (still busy locally),
        // causing permanent work item loss.
        //
        // Fix: Signal() must NOT be called from ReportJobCompleted. The agent will send
        // AgentReady after clearing its slot, which triggers the safe Signal path.
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        var run = CreateRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHubWithOrchestration();
        await hub.ReportJobCompleted("job-1", payload);

        // Agent should still transition to Idle (orchestrator-side registry)
        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);

        // Signal MUST NOT be called — the agent will send AgentReady after clearing its slot
        _mockFacade.Verify(f => f.Signal(), Times.Never);
    }

    #endregion

    public void Dispose()
    {
        foreach (var orchestration in _orchestrationInstances)
            orchestration.Dispose();
    }
}