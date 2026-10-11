using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

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
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

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
        var hub = CreateHub();
        var act = () => hub.ReportOutputLines("job-1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_AddsLinesToOutputLines()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "line1", "line2", "line3" });

        run.OutputLines.Count.Should().Be(3);
        // BoundedConcurrentQueue<T> is IEnumerable<T>
        run.OutputLines.Should().Contain("line1");
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_AppendsLinesThroughFacade()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "alpha", "beta" });

        // AppendOutputLines is the only write path after removing the direct GetOutputBuffer call
        // TODO: The matcher uses Contains checks rather than asserting exact list contents (Count == 2
        // and SequenceEqual). The test would pass if the call included extra elements beyond "alpha"
        // and "beta". Tighten to l.Count == 2 && l[0] == "alpha" && l[1] == "beta" to fully pin
        // the expected call shape.
        _mockFacade.Verify(f => f.AppendOutputLines(
            It.Is<JobId>(j => j.Value == "job-1"),
            It.Is<IReadOnlyList<string>>(l => l.Contains("alpha") && l.Contains("beta"))),
            Times.Once);
    }

    [Fact]
    public async Task ReportOutputLines_WithRun_NotifiesChange()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "line" });

        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Once);
    }

    [Fact]
    public async Task ReportOutputLines_NullRun_StillAppendsThroughFacade()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        await hub.ReportOutputLines("job-1", new[] { "orphan-line" });

        _mockFacade.Verify(f => f.AppendOutputLines(
            It.Is<JobId>(j => j.Value == "job-1"),
            // TODO: This matcher uses Contains rather than asserting exact list contents (Count == 1
            // and exact element). Tighten to l.Count == 1 && l[0] == "orphan-line" to fully pin the
            // expected call shape.
            It.Is<IReadOnlyList<string>>(l => l.Contains("orphan-line"))),
            Times.Once);
        _mockChangeNotifier.Verify(n => n.NotifyChange(), Times.Never);
    }

    [Fact]
    public async Task ReportOutputLines_EmptyList_DoesNotThrow()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

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
    public async Task ReportChatEntry_AppendsEntryThroughFacade()
    {
        ChatEntry? captured = null;
        _mockFacade.Setup(f => f.AppendChatEntry(
                It.Is<JobId>(j => j.Value == "job-1"),
                It.IsAny<ChatEntry>()))
            .Callback<JobId, ChatEntry>((_, e) => captured = e);

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.User, "Hello, agent!");

        captured.Should().NotBeNull();
        captured!.Role.Should().Be(ChatRole.User);
        captured.Content.Should().Be("Hello, agent!");
    }

    [Fact]
    public async Task ReportChatEntry_AgentRole_AppendsWithCorrectRole()
    {
        ChatEntry? captured = null;
        _mockFacade.Setup(f => f.AppendChatEntry(
                It.Is<JobId>(j => j.Value == "job-1"),
                It.IsAny<ChatEntry>()))
            .Callback<JobId, ChatEntry>((_, e) => captured = e);

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.Agent, "Here is my analysis.");

        captured.Should().NotBeNull();
        captured!.Role.Should().Be(ChatRole.Agent);
    }

    [Fact]
    public async Task ReportChatEntry_NullRun_DoesNotThrow()
    {
        // ReportChatEntry no longer calls GetRun — it routes through AppendChatEntry on the facade.
        // No setup needed; Moq's default Loose behaviour makes AppendChatEntry a no-op.
        // TODO: Add _mockFacade.Verify(f => f.AppendChatEntry(It.IsAny<JobId>(), It.IsAny<ChatEntry>()), Times.Once)
        // to assert AppendChatEntry is still called unconditionally even for an unknown job ID.
        // Without this, a silent early-return regression (guard returning before the facade call) would
        // not be caught by this test.
        var hub = CreateHub();
        var act = () => hub.ReportChatEntry("job-1", ChatRole.User, "message");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportChatEntry_SetsTimestamp()
    {
        ChatEntry? captured = null;
        _mockFacade.Setup(f => f.AppendChatEntry(
                It.Is<JobId>(j => j.Value == "job-1"),
                It.IsAny<ChatEntry>()))
            .Callback<JobId, ChatEntry>((_, e) => captured = e);

        var before = DateTimeOffset.UtcNow;

        var hub = CreateHub();
        await hub.ReportChatEntry("job-1", ChatRole.User, "test");

        captured.Should().NotBeNull();
        captured!.Timestamp.Should().BeOnOrAfter(before);
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
    public async Task ReportQualityGateResult_WithRun_CallsAppendQualityGateReport_OnFacade()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var report = FailedReport();

        var hub = CreateHub();
        await hub.ReportQualityGateResult("job-1", report);

        // After the fix, the hub routes through AppendQualityGateReport (not a direct enqueue on run).
        // run.QualityGateHistory is NOT mutated by the hub; the run service owns the history.
        _mockFacade.Verify(f => f.AppendQualityGateReport(
            It.Is<JobId>(j => j.Value == "job-1"),
            report), Times.Once);
    }

    [Fact]
    public async Task ReportQualityGateResult_NullRun_DoesNotThrow()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        var hub = CreateHub();
        var act = () => hub.ReportQualityGateResult("job-1", PassedReport());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportQualityGateResult_MultipleReports_CallsAppendQualityGateReport_ForEach()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "job-1"))).Returns(run);

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

        // LatestQualityReport is still set via ReplaceRun (hash-backed scalar field)
        run.LatestQualityReport.Should().Be(report2, "LatestQualityReport is overwritten each time");

        // Both reports are routed through AppendQualityGateReport, not directly enqueued on run
        _mockFacade.Verify(f => f.AppendQualityGateReport(
            It.Is<JobId>(j => j.Value == "job-1"),
            report1), Times.Once);
        _mockFacade.Verify(f => f.AppendQualityGateReport(
            It.Is<JobId>(j => j.Value == "job-1"),
            report2), Times.Once);
    }

    // ── ReportStepTransition — OrphanRestoredAt clearing ─────────────────

    // TODO: Add a hub-level test that verifies SubscribeToRun populates the RunStateSnapshot with the
    // quality-gate history returned by _facade.GetQualityGateHistoryAsync. A regression that passed an
    // empty list instead of qgHistory to BuildRunStateSnapshot would not be caught by the current suite
    // at the hub unit-test layer. The test should: (1) set up _mockFacade.GetQualityGateHistoryAsync to
    // return a non-empty list, (2) call hub.SubscribeToRun, and (3) verify the snapshot sent to the
    // client contains that history. See issue #3553 spec ("the SubscribeToRun snapshot contains the
    // history returned by GetQualityGateHistoryAsync").

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
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

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
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

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
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        var act = () => hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportStepTransition_WhenOrphanRestoredAtUpdateFaults_LogsWarningWithReportStepTransitionCallerName()
    {
        // Arrange: the orphanRestoredAt Redis write fails.
        // TCS is signalled by the Moq Callback when the matching Warning fires on the thread pool,
        // so the test thread yields instead of spinning — no SpinWait, no CPU pressure.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var agent = CreateAgent();
        // OrphanRestoredAt must be non-null to enter the if-branch that calls UpdateAgentFieldFireAndForget.
        // If null, the if-guard short-circuits, the fault is never triggered, and WaitAsync hangs for 30 s.
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Wire the Callback before the act so the TCS is signalled as soon as the continuation fires.
        // Serilog's Warning<T0,T1,T2>(Exception?, string, T0, T1, T2) overload is matched by concrete types:
        // T0 = string (callerContext), T1 = AgentId, T2 = string (field name).
        // It.IsAny<object>() would NOT match here — use typed matchers or Moq resolves the wrong overload.
        //
        // TODO: The template-string predicate (s.Contains("{CallerContext}") etc.) binds to the internal
        // UpdateFieldFailedTemplate constant in AgentHubFacadeExtensions. If that constant is renamed or
        // reworded the Setup silently stops matching and the test hangs for the full WaitAsync timeout before
        // failing with a TimeoutException rather than a clear assertion error. Consider replacing the substring
        // check with a direct reference to the internal constant (via InternalsVisibleTo or a test-local copy)
        // and/or using Task.WhenAny(warningFired.Task, Task.Delay(timeout)) + Assert.Fail for a clearer failure.
        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx == "ReportStepTransition"),
                It.IsAny<AgentId>(),
                It.Is<string>(f => f == "orphanRestoredAt")))
            .Callback(() => warningFired.TrySetResult(true));

        // TODO: This test constructs AgentHub inline using the shared _mockFacade field because CreateHub()
        // does not accept a custom IAgentJobLifecycleService. If parallel test mutations to _mockFacade ever
        // become a concern (xUnit isolates instances today so this is currently safe), consider extending
        // CreateHub() with an optional lifecycle parameter to remove the boilerplate duplication.
        var mockLifecycle = new Mock<IAgentJobLifecycleService>();
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            mockLifecycle.Object,
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        // Act — Task.FromException produces an already-faulted task, so the ContinueWith callback
        // is queued to the thread pool immediately after ReportStepTransition returns.
        await hub.ReportStepTransition("job-1", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow);

        // Block until the ThreadPool continuation fires (30 s safety net).
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: Warning logged with the exception, the correct callerContext, agent ID, and field name.
        // T2 is pinned to "orphanRestoredAt" to confirm the correct fault path fired.
        //
        // TODO: It.IsAny<Exception>() does not verify the exception is non-null or that it wraps the injected
        // fault. UpdateAgentFieldFireAndForget passes t.Exception?.Flatten() (an AggregateException wrapping
        // the original InvalidOperationException) to the logger. Tighten to
        // It.Is<AggregateException>(ex => ex.InnerException is InvalidOperationException) so a regression
        // that silently drops the exception object while still calling Warning would be caught.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx == "ReportStepTransition"),  // T0 = string (callerContext)
                It.IsAny<AgentId>(),                                   // T1 = AgentId
                It.Is<string>(f => f == "orphanRestoredAt")),          // T2 = string (field name)
            Times.Once);
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
            Mock.Of<IHubConsolidationOperations>(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", "invalid:not-a-real-label");

        mockIssueOps.Verify(s => s.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()),
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
            Mock.Of<IHubConsolidationOperations>(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", string.Empty);

        mockIssueOps.Verify(s => s.SwapLabelAsync(run, string.Empty), Times.Once);
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
            Mock.Of<IHubConsolidationOperations>(),
            mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        await hub.RequestLabelChange("job-1", AgentLabels.EpicApproved);

        mockIssueOps.Verify(
            s => s.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()),
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

// ─────────────────────────────────────────────────────────────────────────────
// Hub-dispatch metric tests (issue #2979)
//
// These tests exercise the PRODUCTION hub dispatch path — they call
// hub.ReportQualityGateResult / hub.ReportPipelineRunEvent directly and verify
// that PipelineTelemetry instruments are incremented, catching bugs in the
// switch/branch logic that purely tautological instrument-level tests would miss.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Tests that <see cref="AgentHub.ReportQualityGateResult"/> records
/// <c>pipeline.run.quality_gate.results</c> via the production
/// <c>RecordQualityGateResultMetrics</c> path (issue #2979).
/// Uses a MeterListener to observe the static shared instruments.
/// </summary>
public sealed class AgentHubReportQualityGateResultMetricsTests
{
    // Counter.Add invokes MeterListener callbacks synchronously on the emitting flow, so tagging each
    // test with its own AsyncLocal value filters out measurements from tests running in parallel.
    private static readonly AsyncLocal<object?> TestFlow = new();

    private readonly Mock<IAgentHubFacade> _mockFacade = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-metric-1");
        hub.Context = mockContext.Object;
        return hub;
    }

    [Fact]
    public async Task ReportQualityGateResult_FlatFieldPassedReport_RecordsPassTagOnStaticInstrument()
    {
        // Arrange: use a MeterListener to observe the shared static instrument.
        var observed = new List<(string gate, string result, string infraFailure)>();
        var flow = new object();
        TestFlow.Value = flow;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.quality_gate.results")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!ReferenceEquals(TestFlow.Value, flow)) return;
            string gate = "", result = "", infra = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gate = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
                if (tag.Key == "infrastructure_failure") infra = tag.Value?.ToString() ?? "";
            }
            observed.Add((gate, result, infra));
        });
        listener.Start();

        var run = new PipelineRun
        {
            RunId = "hub-metric-dispatch-test",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = CodingAgent.Pipeline.Models.PipelineRunType.Implementation
        };
        _mockFacade.Setup(f => f.GetRun("job-metric-1")).Returns(run);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        var hub = CreateHub();

        // Act: call the actual hub method — exercises RecordQualityGateResultMetrics
        await hub.ReportQualityGateResult("job-metric-1", report);

        // Force the listener to collect
        listener.RecordObservableInstruments();

        // Assert: compilation and tests gates were both recorded with result=pass
        observed.Should().Contain(t => t.gate == "compilation" && t.result == "pass" && t.infraFailure == "false",
            "a passing compilation gate must emit gate=compilation, result=pass, infrastructure_failure=false");
        observed.Should().Contain(t => t.gate == "tests" && t.result == "pass" && t.infraFailure == "false",
            "a passing tests gate must emit gate=tests, result=pass, infrastructure_failure=false");
    }

    [Fact]
    public async Task ReportQualityGateResult_FailedTestsGate_RecordsFailTag()
    {
        var observed = new List<(string gate, string result)>();
        var flow = new object();
        TestFlow.Value = flow;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.quality_gate.results")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!ReferenceEquals(TestFlow.Value, flow)) return;
            string gate = "", result = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gate = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            }
            observed.Add((gate, result));
        });
        listener.Start();

        var run = new PipelineRun
        {
            RunId = "hub-fail-test",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = CodingAgent.Pipeline.Models.PipelineRunType.Implementation
        };
        _mockFacade.Setup(f => f.GetRun("job-fail-1")).Returns(run);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };

        var hub = CreateHub();
        await hub.ReportQualityGateResult("job-fail-1", report);
        listener.RecordObservableInstruments();

        observed.Should().Contain(t => t.gate == "tests" && t.result == "fail",
            "a failing tests gate must emit gate=tests, result=fail");
        observed.Should().Contain(t => t.gate == "compilation" && t.result == "pass",
            "a passing compilation gate must emit gate=compilation, result=pass");
    }
}

/// <summary>
/// Covers the multi-QGC path (QgcResults.Count > 0) and the ExternalCi gate path
/// in RecordQualityGateResultMetrics (AgentHub.Lifecycle.cs lines 204-215, 226-227).
/// These paths are not exercised by the existing flat-field tests.
/// </summary>
public sealed class AgentHubReportQualityGateResultMetricsMultiQgcTests
{
    // Counter.Add invokes MeterListener callbacks synchronously on the emitting flow, so tagging each
    // test with its own AsyncLocal value filters out measurements from tests running in parallel.
    private static readonly AsyncLocal<object?> TestFlow = new();

    private readonly Mock<IAgentHubFacade> _mockFacade = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-multiqgc-1");
        hub.Context = mockCtx.Object;
        return hub;
    }

    private PipelineRun CreateRun(string jobId)
        => new()
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#50",
            IssueTitle = "MultiQgc test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = CodingAgent.Pipeline.Models.PipelineRunType.Implementation
        };

    /// <summary>
    /// When QgcResults is populated (multi-QGC mode), RecordQualityGateResultMetrics
    /// must record per-QGC compilation and tests gates instead of the flat-field fallback.
    /// Covers AgentHub.Lifecycle.cs lines 204-215 (the foreach branch).
    /// </summary>
    [Fact]
    public async Task ReportQualityGateResult_WithQgcResults_RecordsPerQgcGates()
    {
        // Use ConcurrentBag to avoid data races from background-thread MeterListener callbacks.
        var observed = new System.Collections.Concurrent.ConcurrentBag<(string gate, string result)>();
        var flow = new object();
        TestFlow.Value = flow;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.quality_gate.results")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!ReferenceEquals(TestFlow.Value, flow)) return;
            string gate = "", result = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gate = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            }
            observed.Add((gate, result));
        });
        listener.Start();

        // Capture baseline count after Start() so any pre-existing emissions from prior
        // tests in the [Collection("Metrics")] group are excluded from the assertion.
        var baselineCount = observed.Count;

        _mockFacade.Setup(f => f.GetRun("job-multiqgc-1")).Returns(CreateRun("job-multiqgc-1"));
        var hub = CreateHub();

        // Build a report with two QGC entries (multi-QGC mode)
        var report = new QualityGateReport
        {
            // Flat fields are present but should NOT be used when QgcResults is populated
            Compilation = new GateResult { GateName = "Compilation", Passed = false },
            Tests = new GateResult { GateName = "Tests", Passed = false },
            QgcResults =
            [
                new QgcExecutionResult
                {
                    QgcId = "qgc-1",
                    DisplayName = "dotnet",
                    Compilation = new GateResult { GateName = "Compilation", Passed = true },
                    Tests = new GateResult { GateName = "Tests", Passed = true }
                },
                new QgcExecutionResult
                {
                    QgcId = "qgc-2",
                    DisplayName = "dotnet-integration",
                    Compilation = new GateResult { GateName = "Compilation", Passed = true },
                    Tests = new GateResult { GateName = "Tests", Passed = false }
                }
            ]
        };

        await hub.ReportQualityGateResult("job-multiqgc-1", report);
        listener.RecordObservableInstruments();

        // Stop the listener before asserting so the callback cannot mutate `observed`
        // while the assertion library enumerates it to build failure messages.
        listener.Dispose();
        // Take only the records added after the baseline to exclude cross-test bleed.
        var snapshot = observed.ToList().Skip(baselineCount).ToList();

        // Two QGC entries → 4 gate records (compilation+tests per QGC)
        snapshot.Should().HaveCount(4,
            "multi-QGC mode must record one compilation and one tests gate per QGC entry (2 QGCs × 2 gates = 4 records)");

        // The second QGC has a failing tests gate — verify it's recorded correctly
        snapshot.Should().Contain(t => t.gate == "tests" && t.result == "fail",
            "a failing tests gate from QgcResults must emit result=fail");
        snapshot.Should().Contain(t => t.gate == "compilation" && t.result == "pass",
            "a passing compilation gate from QgcResults must emit result=pass");
    }

    /// <summary>
    /// When the report includes a non-null ExternalCi gate, RecordQualityGateResultMetrics
    /// must record an external_ci gate metric.
    /// Covers AgentHub.Lifecycle.cs lines 225-227 (the ExternalCi recording branch).
    /// </summary>
    [Fact]
    public async Task ReportQualityGateResult_WithExternalCiGate_RecordsExternalCiMetric()
    {
        var observed = new List<(string gate, string result)>();
        var flow = new object();
        TestFlow.Value = flow;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.quality_gate.results")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!ReferenceEquals(TestFlow.Value, flow)) return;
            string gate = "", result = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gate = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            }
            observed.Add((gate, result));
        });
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-extci-1")).Returns(CreateRun("job-extci-1"));
        var hub = CreateHub();

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            ExternalCi = new GateResult { GateName = "External CI", Passed = true }
        };

        await hub.ReportQualityGateResult("job-extci-1", report);
        listener.RecordObservableInstruments();

        observed.Should().Contain(t => t.gate == "external_ci" && t.result == "pass",
            "a passing ExternalCi gate must emit gate=external_ci, result=pass");
    }
}
/// metric instrument for each <see cref="PipelineRunEventKind"/> (issue #2979).
/// Uses a MeterListener to observe the static shared instruments, exercising the
/// production switch-dispatch in <c>AgentHub.Lifecycle.cs</c>.
/// </summary>
public sealed class AgentHubReportPipelineRunEventDispatchTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-dispatch-1");
        hub.Context = mockCtx.Object;
        return hub;
    }

    private PipelineRun CreateRun(string jobId, CodingAgent.Pipeline.Models.PipelineRunType runType
        = CodingAgent.Pipeline.Models.PipelineRunType.Implementation)
        => new()
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Dispatch test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = runType
        };

    [Fact]
    public async Task CiNotStartedRetrigger_DispatchesTo_RunCiNotStartedRetriggers_Counter()
    {
        // Arrange: observe pipeline.run.ci.not_started_retriggers on the static instrument.
        var counterHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.not_started_retriggers")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => counterHit = true);
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-retriger-1")).Returns(CreateRun("job-retriger-1"));
        var hub = CreateHub();

        // Act: call hub with a CiNotStartedRetrigger event
        await hub.ReportPipelineRunEvent("job-retriger-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiNotStartedRetrigger
        });
        listener.RecordObservableInstruments();

        // Assert: the counter was incremented through the hub dispatch path
        counterHit.Should().BeTrue(
            "ReportPipelineRunEvent with Kind=CiNotStartedRetrigger must increment " +
            "pipeline.run.ci.not_started_retriggers via the hub switch dispatch");
    }

    [Fact]
    public async Task CiWait_WithAllRequiredFields_DispatchesTo_RunCiWait_Histogram()
    {
        // Arrange: observe pipeline.run.ci.wait
        var recorded = new List<(double value, string stage, string result)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.wait")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string stage = "", result = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "stage") stage = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            }
            recorded.Add((value, stage, result));
        });
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-ciwait-1")).Returns(CreateRun("job-ciwait-1"));
        var hub = CreateHub();

        // Act
        await hub.ReportPipelineRunEvent("job-ciwait-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiWait,
            DurationSeconds = 450.0,
            Stage = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.CiWaitStages.PrePr,
            Result = "pass"
        });
        listener.RecordObservableInstruments();

        // Assert: histogram was recorded with the correct value and tags
        recorded.Should().HaveCount(1,
            "exactly one CiWait observation must be recorded for a valid CiWait event");
        recorded[0].value.Should().Be(450.0, "DurationSeconds must be forwarded to the histogram");
        recorded[0].stage.Should().Be("pre_pr", "Stage=pre_pr must be forwarded to the stage tag");
        recorded[0].result.Should().Be("pass", "Result=pass must be forwarded to the result tag");
    }

    [Fact]
    public async Task CiWait_WhenKindSwitchedToWrongCase_WouldNotRecord_Demonstrating_HubIsActuallyExercised()
    {
        // This test verifies that the CiNotStartedRetrigger kind does NOT record to ci.wait.
        // If the hub's switch dispatch were wrong (e.g. CiNotStartedRetrigger → ci.wait branch),
        // this test would fail — proving the hub is actually tested here, not just the instrument.
        var ciWaitHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.wait")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => ciWaitHit = true);
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-wrong-kind-1")).Returns(CreateRun("job-wrong-kind-1"));
        var hub = CreateHub();

        // Send a CiNotStartedRetrigger event — must NOT record into ci.wait histogram
        await hub.ReportPipelineRunEvent("job-wrong-kind-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiNotStartedRetrigger
        });
        listener.RecordObservableInstruments();

        ciWaitHit.Should().BeFalse(
            "CiNotStartedRetrigger events must not record into pipeline.run.ci.wait");
    }

    [Fact]
    public async Task AgentStall_WithPhaseAndKind_DispatchesTo_RunAgentStalls_Counter()
    {
        // Arrange
        var recorded = new List<(string phase, string kind)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.agent_stalls")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string phase = "", kind = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                if (tag.Key == "kind") kind = tag.Value?.ToString() ?? "";
            }
            recorded.Add((phase, kind));
        });
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-stall-1")).Returns(CreateRun("job-stall-1"));
        var hub = CreateHub();

        // Act: send an AgentStall event with stall_kill kind
        await hub.ReportPipelineRunEvent("job-stall-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.RunPhases.CodeGen,
            Result = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.AgentStallKinds.StallKill
        });
        listener.RecordObservableInstruments();

        // Assert: counter was incremented with correct phase and kind tags
        recorded.Should().HaveCount(1,
            "exactly one agent_stalls observation must be recorded for a valid AgentStall event");
        recorded[0].phase.Should().Be("codegen",
            "Stage=codegen must be forwarded to the phase tag");
        recorded[0].kind.Should().Be("stall_kill",
            "Result=stall_kill must be forwarded to the kind tag");
    }

    [Theory]
    // Phase names older agent images still send map onto the shared phase set.
    [InlineData("qgc_retry_agent", "process_timeout", "quality_gate")]
    [InlineData("code_review", "stall_kill", "review")]
    [InlineData("unknown", "process_death", "other")]
    // Arbitrary strings cannot add label values.
    [InlineData("some agent-chosen phase", "STALL_KILL", "other")]
    public async Task AgentStall_NormalizesPhaseAndKindToClosedSets(string stage, string kind, string expectedPhase)
    {
        var recorded = new System.Collections.Concurrent.ConcurrentQueue<(string phase, string kind)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Only the real instrument: other tests create look-alike counters on test meters.
            if (ReferenceEquals(instrument, CodingAgent.Pipeline.Telemetry.PipelineTelemetry.RunAgentStalls))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (value == 0) return;
            string phase = "", recordedKind = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                if (tag.Key == "kind") recordedKind = tag.Value?.ToString() ?? "";
            }
            recorded.Enqueue((phase, recordedKind));
        });
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-stall-norm")).Returns(CreateRun("job-stall-norm"));
        var hub = CreateHub();

        await hub.ReportPipelineRunEvent("job-stall-norm", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = stage,
            Result = kind
        });

        recorded.Should().ContainSingle().Which.Should().Be((expectedPhase, kind.ToLowerInvariant()));
    }

    [Fact]
    public async Task AgentStall_UnknownKind_DoesNotRecord()
    {
        var recorded = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, CodingAgent.Pipeline.Telemetry.PipelineTelemetry.RunAgentStalls))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (value != 0) Interlocked.Increment(ref recorded);
        });
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-stall-rogue")).Returns(CreateRun("job-stall-rogue"));
        var hub = CreateHub();

        await hub.ReportPipelineRunEvent("job-stall-rogue", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.RunPhases.CodeGen,
            Result = "rogue_kind"
        });

        recorded.Should().Be(0, "a kind outside the closed set must not be recorded");
    }

    [Fact]
    public async Task AgentStall_MissingStage_DoesNotRecord()
    {
        // Arrange: AgentStall without Stage should be silently skipped (logged as warning)
        var stallHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.agent_stalls")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => stallHit = true);
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-stall-missing-1")).Returns(CreateRun("job-stall-missing-1"));
        var hub = CreateHub();

        // Send AgentStall with missing Stage (null) — hub must skip, not throw
        var act = () => hub.ReportPipelineRunEvent("job-stall-missing-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = null,
            Result = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.AgentStallKinds.StallKill
        });

        await act.Should().NotThrowAsync("missing Stage must be silently skipped, not throw");
        listener.RecordObservableInstruments();
        stallHit.Should().BeFalse("an AgentStall event with null Stage must not record any metric");
    }

    /// <summary>
    /// When <see cref="IAgentHubFacade.GetRun"/> returns null (run evicted between
    /// [RequiresActiveJob] and the hub body), the runType must default to Implementation
    /// and the counter must still be incremented without throwing.
    /// Covers the null-run fallback path (AgentHub.Lifecycle.cs lines 203-215).
    /// </summary>
    [Fact]
    public async Task WhenRunIsNull_DefaultsToImplementationRunType_AndStillRecordsMetric()
    {
        var counterHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.not_started_retriggers")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            // run_type must be "implementation" — the fallback when run is null
            foreach (var tag in tags)
                if (tag.Key == "run_type" && tag.Value?.ToString() == "implementation")
                    counterHit = true;
        });
        listener.Start();

        // GetRun returns null to exercise the fallback path
        _mockFacade.Setup(f => f.GetRun("job-null-run-1")).Returns((PipelineRun?)null);
        var hub = CreateHub();

        await hub.ReportPipelineRunEvent("job-null-run-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiNotStartedRetrigger
        });
        listener.RecordObservableInstruments();

        counterHit.Should().BeTrue(
            "when GetRun returns null, the metric must still be recorded with run_type=implementation");
    }

    /// <summary>
    /// CiWait events missing DurationSeconds must be silently skipped — no metric, no throw.
    /// Covers the warning path in the CiWait branch (AgentHub.Lifecycle.cs line 226-227).
    /// </summary>
    [Fact]
    public async Task CiWait_MissingDurationSeconds_DoesNotRecordAndDoesNotThrow()
    {
        var ciWaitHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.wait")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => ciWaitHit = true);
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-ciwait-missing-1")).Returns(CreateRun("job-ciwait-missing-1"));
        var hub = CreateHub();

        // CiWait without DurationSeconds must be skipped
        var act = () => hub.ReportPipelineRunEvent("job-ciwait-missing-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.CiWait,
            DurationSeconds = null,
            Stage = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.CiWaitStages.PrePr,
            Result = "pass"
        });

        await act.Should().NotThrowAsync("missing DurationSeconds must be silently skipped, not throw");
        listener.RecordObservableInstruments();
        ciWaitHit.Should().BeFalse("a CiWait event with null DurationSeconds must not record any metric");
    }

    /// <summary>
    /// AgentStall events missing Result must be silently skipped — no metric, no throw.
    /// Covers the warning path in the AgentStall branch (AgentHub.Lifecycle.cs lines 281-284).
    /// </summary>
    [Fact]
    public async Task AgentStall_MissingResult_DoesNotRecordAndDoesNotThrow()
    {
        var stallHit = false;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.agent_stalls")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => stallHit = true);
        listener.Start();

        _mockFacade.Setup(f => f.GetRun("job-stall-noResult-1")).Returns(CreateRun("job-stall-noResult-1"));
        var hub = CreateHub();

        // AgentStall with null Result — must be silently skipped
        var act = () => hub.ReportPipelineRunEvent("job-stall-noResult-1", new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.AgentStall,
            Stage = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.RunPhases.CodeGen,
            Result = null
        });

        await act.Should().NotThrowAsync("missing Result must be silently skipped, not throw");
        listener.RecordObservableInstruments();
        stallHit.Should().BeFalse("an AgentStall event with null Result must not record any metric");
    }

    /// <summary>
    /// Unknown PipelineRunEventKind values must be silently ignored — no throw.
    /// Covers the default branch in the ReportPipelineRunEvent switch
    /// (AgentHub.Lifecycle.cs lines 308-310).
    /// </summary>
    [Fact]
    public async Task UnknownKind_IsIgnoredWithoutThrowing()
    {
        _mockFacade.Setup(f => f.GetRun("job-unknown-kind-1")).Returns(CreateRun("job-unknown-kind-1"));
        var hub = CreateHub();

        // Cast an out-of-range value to the enum to simulate an unknown kind
        var unknownKind = (PipelineRunEventKind)999;
        var act = () => hub.ReportPipelineRunEvent("job-unknown-kind-1", new PipelineRunEventReport
        {
            Kind = unknownKind
        });

        await act.Should().NotThrowAsync("unknown PipelineRunEventKind values must be silently ignored");
    }
}
