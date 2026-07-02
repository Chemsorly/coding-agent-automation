using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
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
            _mockLabelSwapper.Object,
            _mockLifecycleManager.Object,
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
        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ReportJobCompleted_TransitionsAgentToIdle_SignalsDrain()
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
        _mockFacade.Verify(f => f.Signal(), Times.Once);
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
        _mockFacade.Verify(f => f.Signal(), Times.Once);
        agent.ActiveJobId.Should().BeNull();
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

        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>()), Times.Once);
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

        _mockLifecycleManager.Verify(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region JobRejected

    [Fact]
    public async Task JobRejected_ResetsAgentToIdle_SignalsDrain()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "job-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
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

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync("issue-cfg-1", "org/repo#42", "agent:error", LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_UnknownRun_ReturnsEarly()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        await hub.RequestLabelChange("job-1", "agent:error");

        _mockLabelSwapper.Verify(s => s.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region RequestPostComment

    [Fact]
    public async Task RequestPostComment_AnalysisType_PostsMarkdown()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var issueConfig = new ProviderConfig { Id = "issue-cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("issue-cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var mockIssueProvider = new Mock<IIssueProvider>();
        _mockFacade.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "## Analysis\nLooks good." };
        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        mockIssueProvider.Verify(p => p.PostCommentAsync("org/repo#42", "## Analysis\nLooks good.", It.IsAny<CancellationToken>()), Times.Once);
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
            _mockLabelSwapper.Object,
            _mockLifecycleManager.Object,
            _mockLogger.Object);

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private PipelineOrchestrationService CreateMinimalOrchestrationService()
    {
        var service = new PipelineOrchestrationService(
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            new IssueDescriptionParser(),
            Mock.Of<IAgentPhaseExecutor>(),
            Mock.Of<IQualityGateExecutor>(),
            _mockLogger.Object,
            brainUpdateService: Mock.Of<IBrainUpdateService>(),
            historyService: Mock.Of<IPipelineRunHistoryService>(),
            runService: Mock.Of<IOrchestratorRunService>());
        _orchestrationInstances.Add(service);
        return service;
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

    public void Dispose()
    {
        foreach (var orchestration in _orchestrationInstances)
            orchestration.Dispose();
    }
}