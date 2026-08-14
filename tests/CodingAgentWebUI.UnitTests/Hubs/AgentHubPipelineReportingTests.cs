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

/// <summary>
/// Tests for AgentHub pipeline reporting methods:
/// ReportBrainSyncResult, ReportOutputLines, ReportChatEntry, ReportQualityGateResult,
/// and the OrphanRestoredAt-clearing path in ReportStepTransition.
/// These methods were changed by the JobId strong-type migration and were previously uncovered.
/// </summary>
public sealed class AgentHubPipelineReportingTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IChangeNotifier> _mockChangeNotifier = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,  // ModelFetchService
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private static PipelineRun CreateRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    private static AgentEntry CreateAgent(string agentId = "agent-1", string connectionId = "conn-1") => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static QualityGateReport PassedReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true },
        Tests = new GateResult { GateName = "Tests", Passed = true }
    };

    private static QualityGateReport FailedReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false },
        Tests = new GateResult { GateName = "Tests", Passed = false }
    };

    // ── ReportBrainSyncResult ─────────────────────────────────────────────

    [Fact]
    public async Task ReportBrainSyncResult_WithRun_UpdatesContextFields()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportBrainSyncResult("job-1", contextLoaded: true, knowledgeFileCount: 5);

        run.BrainContextLoaded.Should().BeTrue();
        run.BrainKnowledgeFileCount.Should().Be(5);
    }

    [Fact]
    public async Task ReportBrainSyncResult_WithRun_NotifiesChange()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportBrainSyncResult("job-1", contextLoaded: false, knowledgeFileCount: 0);

        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task ReportBrainSyncResult_NullRun_DoesNotThrow()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.ReportBrainSyncResult("job-1", contextLoaded: true, knowledgeFileCount: 3);

        await act.Should().NotThrowAsync();
        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    [Fact]
    public async Task ReportBrainSyncResult_ContextNotLoaded_SetsLoadedFalse()
    {
        var run = CreateRun();
        run.BrainContextLoaded = true;
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportBrainSyncResult("job-1", contextLoaded: false, knowledgeFileCount: 0);

        run.BrainContextLoaded.Should().BeFalse();
        run.BrainKnowledgeFileCount.Should().Be(0);
    }

    // ── ReportOutputLines ─────────────────────────────────────────────────

    [Fact]
    public async Task ReportOutputLines_NullLines_Throws()
    {
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetOutputBuffer(It.IsAny<JobId>())).Returns(buffer);

        var hub = CreateHub();
        var act = () => hub.ReportOutputLines("job-1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_AddsLinesToOutputLines()
    {
        var run = CreateRun();
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetOutputBuffer("job-1")).Returns(buffer);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "line1", "line2", "line3" });

        run.OutputLines.Count.Should().Be(3);
        // BoundedConcurrentQueue<T> is IEnumerable<T>
        run.OutputLines.Should().Contain("line1");
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_AddsLinesToOutputBuffer()
    {
        var run = CreateRun();
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetOutputBuffer("job-1")).Returns(buffer);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "alpha", "beta" });

        buffer.GetAll().Should().Contain("alpha").And.Contain("beta");
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_NotifiesChange()
    {
        var run = CreateRun();
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetOutputBuffer("job-1")).Returns(buffer);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "line" });

        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task ReportOutputLines_NullRun_StillAddsToBuffer()
    {
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetOutputBuffer("job-1")).Returns(buffer);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "orphan-line" });

        buffer.GetAll().Should().Contain("orphan-line");
        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    [Fact]
    public async Task ReportOutputLines_EmptyList_DoesNotThrow()
    {
        var run = CreateRun();
        var buffer = new OutputRingBuffer();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetOutputBuffer("job-1")).Returns(buffer);

        var hub = CreateHub();
        var act = () => hub.ReportOutputLines("job-1", Array.Empty<string>());

        await act.Should().NotThrowAsync();
        run.OutputLines.Count.Should().Be(0);
    }

    // ── ReportChatEntry ───────────────────────────────────────────────────

    [Fact]
    public async Task ReportChatEntry_NullContent_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.ReportChatEntry("job-1", ChatRole.User, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportChatEntry_WithRun_EnqueuesChatEntry()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.User, "Hello, agent!");

        run.ChatHistory.Count.Should().Be(1);
        // BoundedConcurrentQueue<T> is IEnumerable<T>
        var entry = run.ChatHistory.First();
        entry.Role.Should().Be(ChatRole.User);
        entry.Content.Should().Be("Hello, agent!");
    }

    [Fact]
    public async Task ReportChatEntry_AgentRole_EnqueuesWithCorrectRole()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.Agent, "Here is my analysis.");

        run.ChatHistory.First().Role.Should().Be(ChatRole.Agent);
    }

    [Fact]
    public async Task ReportChatEntry_NullRun_DoesNotThrow()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.ReportChatEntry("job-1", ChatRole.User, "message");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportChatEntry_SetsTimestamp()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var before = DateTime.UtcNow;

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.User, "test");

        run.ChatHistory.First().Timestamp.Should().BeOnOrAfter(before);
    }

    // ── ReportQualityGateResult ───────────────────────────────────────────

    [Fact]
    public async Task ReportQualityGateResult_NullReport_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.ReportQualityGateResult("job-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportQualityGateResult_WithRun_SetsLatestReport()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var report = PassedReport();

        var hub = CreateHub();
        await hub.ReportQualityGateResult("job-1", report);

        run.LatestQualityReport.Should().Be(report);
    }

    [Fact]
    public async Task ReportQualityGateResult_WithRun_EnqueuesInHistory()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var report = FailedReport();

        var hub = CreateHub();
        await hub.ReportQualityGateResult("job-1", report);

        run.QualityGateHistory.Count.Should().Be(1);
        run.QualityGateHistory.First().Should().Be(report);
    }

    [Fact]
    public async Task ReportQualityGateResult_MultipleReports_AllEnqueued()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportQualityGateResult("job-1", FailedReport());
        await hub.ReportQualityGateResult("job-1", PassedReport());

        run.QualityGateHistory.Count.Should().Be(2);
        run.LatestQualityReport!.AllPassed.Should().BeTrue("last report should win");
    }

    [Fact]
    public async Task ReportQualityGateResult_NullRun_DoesNotThrow()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        var hub = CreateHub();
        var act = () => hub.ReportQualityGateResult("job-1", PassedReport());
        await act.Should().NotThrowAsync();
    }

    // ── ReportStepTransition — OrphanRestoredAt clearing ─────────────────

    [Fact]
    public async Task ReportStepTransition_AgentWithOrphanRestoredAt_ClearsIt()
    {
        var run = CreateRun();
        var agent = CreateAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var mockLifecycle = new Mock<IAgentJobLifecycleService>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);

        agent.OrphanRestoredAt.Should().BeNull("active progress should clear the orphan-restored flag");
    }

    [Fact]
    public async Task ReportStepTransition_AgentWithoutOrphanRestoredAt_RemainsNull()
    {
        var run = CreateRun();
        var agent = CreateAgent();
        agent.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var mockLifecycle = new Mock<IAgentJobLifecycleService>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);

        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public async Task ReportStepTransition_NullAgent_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var mockLifecycle = new Mock<IAgentJobLifecycleService>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        var act = () => hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);
        await act.Should().NotThrowAsync();
    }

    // ── RequestLabelChange — invalid label path ───────────────────────────

    [Fact]
    public async Task RequestLabelChange_InvalidLabel_ReturnsEarlyWithoutSwapping()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var mockIssueOps = new Mock<IHubIssueOperations>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", "invalid:not-a-real-label");

        mockIssueOps.Verify(s => s.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestLabelChange_EmptyLabel_SwapsWithoutValidation()
    {
        // Empty label is allowed through the label-validation guard
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var mockIssueOps = new Mock<IHubIssueOperations>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", string.Empty);

        mockIssueOps.Verify(s => s.SwapLabelAsync(run, string.Empty, It.IsAny<LabelTargetKind>()), Times.Once);
    }

    // TODO: Add a test that passes a valid, non-gated pipeline label (e.g. AgentLabels.Done) and
    // asserts that SwapLabelAsync IS called. This covers the acceptance criterion
    // "RequestLabelChange with a non-gated label continues to work as before" and would catch
    // regressions where DispatchGatedLabels accidentally matches too broadly or the condition
    // logic is inverted. See review finding on AgentHubPipelineReportingTests line ~503.

    [Fact]
    public async Task RequestLabelChange_EpicApproved_IsIgnored_WithWarning()
    {
        // agent:epic-approved is a valid pipeline label (in AgentLabels.All) but is human-gated.
        // RequestLabelChange must reject it, log a Warning, and never call SwapLabelAsync.
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var mockIssueOps = new Mock<IHubIssueOperations>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", AgentLabels.EpicApproved);

        mockIssueOps.Verify(
            s => s.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<LabelTargetKind>()),
            Times.Never,
            "SwapLabelAsync must not be called for a human-gated label");

        // Serilog resolves Warning("...", newLabel, jobId.Value) to Warning<T0,T1>(string, T0, T1).
        // Moq must match the exact generic overload. Use It.IsAny on all arguments to reliably
        // intercept the call, then verify it was called exactly once.
        // TODO: Tighten the template argument from It.IsAny<string>() to
        // It.Is<string>(s => s.Contains("gated") || s.Contains("epic-approved")) so the assertion
        // exclusively verifies the gated-label rejection Warning rather than any Warning<string,string>
        // call. The current loose matcher becomes fragile if a future guard added before the gated-label
        // check also logs a two-argument Warning (it could produce false passes or spurious failures).
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once,
            "A Warning must be logged when a gated label is rejected");
    }
}
