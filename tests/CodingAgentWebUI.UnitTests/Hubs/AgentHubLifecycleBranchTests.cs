using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Branch coverage tests for AgentHub.Lifecycle.cs:
/// ReportBrainSyncResult, ReportOutputLines, ReportChatEntry, ReportQualityGateResult,
/// and the orphan-state clearing path in ReportStepTransition.
/// </summary>
public sealed class AgentHubLifecycleBranchTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IAgentJobLifecycleService> _lifecycleService = new();
    private readonly Mock<IAgentTokenRefreshService> _tokenRefreshService = new();
    private readonly Mock<IGateCommentFormatter> _gateCommentFormatter = new();
    private readonly Mock<IAgentOrphanRecoveryService> _orphanRecoveryService = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly ModelFetchService _modelFetchService;

    public AgentHubLifecycleBranchTests()
    {
        var registry = new AgentRegistryService(Log.Logger);
        var agentComm = new Mock<IAgentCommunication>();
        _modelFetchService = new ModelFetchService(registry, agentComm.Object, Log.Logger);
    }

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
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
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static AgentEntry CreateAgent(
        string agentId = "agent-1",
        string connectionId = "conn-1",
        DateTimeOffset? orphanRestoredAt = null) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host",
        Labels = [],
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        OrphanRestoredAt = orphanRestoredAt
    };

    private static PipelineRun CreateRun(string runId = "job-1") => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };

    // ── ReportBrainSyncResult — run exists ───────────────────────────────

    [Fact]
    public async Task ReportBrainSyncResult_RunExists_UpdatesRunFields()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var hub = CreateHub();
        await hub.ReportBrainSyncResult(new JobId { Value = "job-1" }, contextLoaded: true, knowledgeFileCount: 42);

        run.BrainContextLoaded.Should().BeTrue();
        run.BrainKnowledgeFileCount.Should().Be(42);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task ReportBrainSyncResult_RunExists_ContextNotLoaded_SetsCorrectValues()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var hub = CreateHub();
        await hub.ReportBrainSyncResult(new JobId { Value = "job-1" }, contextLoaded: false, knowledgeFileCount: 0);

        run.BrainContextLoaded.Should().BeFalse();
        run.BrainKnowledgeFileCount.Should().Be(0);
    }

    // ── ReportBrainSyncResult — run is null (no run in memory) ───────────

    [Fact]
    public async Task ReportBrainSyncResult_RunNull_DoesNotThrow_SkipsNotify()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.ReportBrainSyncResult(new JobId { Value = "job-ghost" }, true, 5);

        await act.Should().NotThrowAsync("null run is a valid state — hub must handle it gracefully");
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    // ── ReportOutputLines — run exists ────────────────────────────────────

    [Fact]
    public async Task ReportOutputLines_RunExists_EnqueuesLinesAndNotifies()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);
        _facade.Setup(f => f.GetOutputBuffer(It.Is<JobId>(j => j.Value == "job-1")))
            .Returns(new OutputRingBuffer());

        var hub = CreateHub();
        await hub.ReportOutputLines(new JobId { Value = "job-1" }, new[] { "line1", "line2" });

        run.OutputLines.Count.Should().Be(2);
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task ReportOutputLines_RunNull_DoesNotThrow_BufferStillFilled()
    {
        var buffer = new OutputRingBuffer();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetOutputBuffer(It.IsAny<JobId>())).Returns(buffer);

        var hub = CreateHub();
        var act = () => hub.ReportOutputLines(new JobId { Value = "job-ghost" }, new[] { "line1" });

        await act.Should().NotThrowAsync("null run must not throw");
        buffer.Count.Should().Be(1, "buffer should still be filled even when run is null");
        _changeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    // ── ReportChatEntry — run exists ──────────────────────────────────────

    [Fact]
    public async Task ReportChatEntry_RunExists_EnqueuesChatEntry()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var hub = CreateHub();
        await hub.ReportChatEntry(new JobId { Value = "job-1" }, ChatRole.Agent, "Hello from agent");

        run.ChatHistory.Count.Should().Be(1);
        var entry = run.ChatHistory.First();
        entry.Content.Should().Be("Hello from agent");
        entry.Role.Should().Be(ChatRole.Agent);
    }

    [Fact]
    public async Task ReportChatEntry_RunNull_DoesNotThrow()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.ReportChatEntry(new JobId { Value = "job-ghost" }, ChatRole.User, "test");

        await act.Should().NotThrowAsync("null run must be handled gracefully");
    }

    [Fact]
    public async Task ReportChatEntry_UserRole_EnqueuesWithCorrectRole()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var hub = CreateHub();
        await hub.ReportChatEntry(new JobId { Value = "job-1" }, ChatRole.User, "User said this");

        var entry = run.ChatHistory.First();
        entry.Role.Should().Be(ChatRole.User);
        entry.Content.Should().Be("User said this");
    }

    // ── ReportQualityGateResult — run exists ──────────────────────────────

    [Fact]
    public async Task ReportQualityGateResult_RunExists_SetsLatestReportAndEnqueues()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        var hub = CreateHub();
        await hub.ReportQualityGateResult(new JobId { Value = "job-1" }, report);

        run.LatestQualityReport.Should().Be(report);
        run.QualityGateHistory.Count.Should().Be(1);
    }

    [Fact]
    public async Task ReportQualityGateResult_RunNull_DoesNotThrow()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };

        var hub = CreateHub();
        var act = () => hub.ReportQualityGateResult(new JobId { Value = "job-ghost" }, report);

        await act.Should().NotThrowAsync("null run must be handled gracefully");
    }

    [Fact]
    public async Task ReportQualityGateResult_MultipleReports_AllEnqueued()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

        var report1 = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };
        var report2 = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        var hub = CreateHub();
        await hub.ReportQualityGateResult(new JobId { Value = "job-1" }, report1);
        await hub.ReportQualityGateResult(new JobId { Value = "job-1" }, report2);

        run.QualityGateHistory.Count.Should().Be(2);
        run.LatestQualityReport.Should().Be(report2, "LatestQualityReport is overwritten each time");
    }

    // ── ReportStepTransition — orphan-restored state cleared ─────────────

    [Fact]
    public async Task ReportStepTransition_AgentHasOrphanRestoredAt_ClearsIt()
    {
        var run = CreateRun();
        var orphanedAgent = CreateAgent(orphanRestoredAt: DateTimeOffset.UtcNow.AddMinutes(-30));

        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(orphanedAgent);

        _lifecycleService.Setup(s => s.HandleStepTransition(
            It.IsAny<JobId>(), It.IsAny<PipelineStep>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<Dictionary<string, string>?>()));

        var hub = CreateHub();
        await hub.ReportStepTransition(
            new JobId { Value = "job-1" },
            PipelineStep.GeneratingCode,
            DateTimeOffset.UtcNow);

        orphanedAgent.OrphanRestoredAt.Should().BeNull(
            "progress report must clear the orphan-restored timestamp");
    }

    [Fact]
    public async Task ReportStepTransition_AgentNoOrphanState_DoesNotThrow()
    {
        var run = CreateRun();
        var regularAgent = CreateAgent(orphanRestoredAt: null);

        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(regularAgent);

        _lifecycleService.Setup(s => s.HandleStepTransition(
            It.IsAny<JobId>(), It.IsAny<PipelineStep>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<Dictionary<string, string>?>()));

        var hub = CreateHub();
        var act = () => hub.ReportStepTransition(
            new JobId { Value = "job-1" },
            PipelineStep.GeneratingCode,
            DateTimeOffset.UtcNow);

        await act.Should().NotThrowAsync("no orphan state — must be a no-op for the clearing logic");
        regularAgent.OrphanRestoredAt.Should().BeNull("was null before, remains null");
    }
}
