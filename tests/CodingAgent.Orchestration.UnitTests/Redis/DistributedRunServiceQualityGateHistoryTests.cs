using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgent.Orchestration.UnitTests.Redis;

/// <summary>
/// Repro tests for issue #3553: quality-gate reports reported through AgentHub.ReportQualityGateResult
/// must survive the Redis round-trip in DistributedRunService.
/// </summary>
public sealed class DistributedRunServiceQualityGateHistoryTests
{
    private readonly FakeRedisStore _store = new();
    private readonly DistributedRunService _sut;

    public DistributedRunServiceQualityGateHistoryTests()
    {
        _sut = new DistributedRunService(_store, (_, _, _) => Task.FromResult(false), Log.Logger);
    }

    private static PipelineRun MakeRun(string runId = "run-qg") => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
    };

    private static QualityGateReport FailedReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false },
        Tests = new GateResult { GateName = "Tests", Passed = false },
    };

    private static QualityGateReport PassedReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true },
        Tests = new GateResult { GateName = "Tests", Passed = true },
    };

    // ── Repro test (primary regression guard) ────────────────────────────

    /// <summary>
    /// Repro for issue #3553: quality-gate reports go through AgentHub → facade → run service.
    /// In Redis mode the GetRun result is a copy, so enqueuing on it is lost. This test
    /// must fail (redis mode) before the fix and pass after.
    /// </summary>
    [Theory]
    [InlineData("in-memory")]
    [InlineData("redis")]
    public async Task Repro_QualityGateReportedThroughAgentHubIsReturnedByRemoveRun(string mode)
    {
        IOrchestratorRunService runService = mode == "redis"
            ? new DistributedRunService(new FakeRedisStore(), (_, _, _) => Task.FromResult(false), Log.Logger)
            : new OrchestratorRunService(Log.Logger);

        var facade = new AgentHubFacade(new AgentHubFacadeDependencies(
            Mock.Of<IAgentRegistryService>(),
            runService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IProviderConfigStore>(),
            Mock.Of<IProviderFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentHubFacadeDependencies>.Instance));

        var hub = new AgentHub(new AgentHubDependencies(
            facade,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            CreateNoOpHubContext()));

        var jobId = Guid.NewGuid().ToString();
        runService.AddRun(new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
        });

        var report = FailedReport();

        // Production write path for quality-gate history (agent -> hub -> facade -> run service).
        await hub.ReportQualityGateResult(jobId, report);

        // TODO: Add `await Task.Yield()` here (like AppendQualityGateReport_ThenGetQualityGateHistoryAsync
        // does) to document that the assertions below rely on FakeRedisStore returning already-completed
        // tasks (synchronous), making the fire-and-forget write in AppendQualityGateReport complete before
        // GetQualityGateHistoryAsync is called. If FakeRedisStore is ever made genuinely async, this test
        // would become racy without the yield. Add the yield as an explicit synchronization hint.

        // Also assert GetQualityGateHistoryAsync returns the entry (covers SubscribeToRun snapshot path).
        var history = await runService.GetQualityGateHistoryAsync(new RunId(jobId));
        history.Should().ContainSingle(
            "a quality-gate report appended via the hub must be returned by GetQualityGateHistoryAsync ({0})", mode);

        // RunLifecycleManager claims the finished run through RemoveRun.
        var removed = runService.RemoveRun(new RunId(jobId));

        removed.Should().NotBeNull();
        removed!.QualityGateHistory.Count.Should().Be(1,
            "a quality-gate report reported during the run must be part of the run RemoveRun returns ({0})", mode);
        removed.QualityGateHistory.First().Compilation.Passed.Should().BeFalse(
            "the returned history must contain the exact report that was reported ({0})", mode);
    }

    // ── AppendQualityGateReport / GetQualityGateHistoryAsync ─────────────

    [Fact]
    public async Task AppendQualityGateReport_ThenGetQualityGateHistoryAsync_ReturnsReportsOldestFirst()
    {
        _sut.AddRun(MakeRun("run-qg-order"));
        var runId = new RunId("run-qg-order");

        var r1 = FailedReport();
        var r2 = PassedReport();
        _sut.AppendQualityGateReport(runId, r1);
        _sut.AppendQualityGateReport(runId, r2);
        await Task.Yield(); // allow fire-and-forget to settle (FakeRedisStore is synchronous)

        var history = await _sut.GetQualityGateHistoryAsync(runId);

        history.Should().HaveCount(2, "both appended reports must be returned");
        history[0].Compilation.Passed.Should().BeFalse("report1 is oldest (failed)");
        history[1].Compilation.Passed.Should().BeTrue("report2 is newest (passed)");
    }

    [Fact]
    public async Task GetQualityGateHistoryAsync_UnknownRunId_ReturnsEmptyList()
    {
        var history = await _sut.GetQualityGateHistoryAsync(new RunId("run-never-added"));
        history.Should().BeEmpty("GetQualityGateHistoryAsync must return empty for an unknown RunId");
    }

    // ── Cap enforcement ───────────────────────────────────────────────────

    [Fact]
    public async Task AppendQualityGateReport_CapEnforced_ListTrimmedToCapacity()
    {
        _sut.AddRun(MakeRun("run-qg-cap"));
        var runId = new RunId("run-qg-cap");

        var overCount = PipelineConstants.DefaultQualityGateHistoryCapacity + 5;
        for (var i = 0; i < overCount; i++)
            _sut.AppendQualityGateReport(runId, i % 2 == 0 ? PassedReport() : FailedReport());

        await Task.Yield(); // allow fire-and-forget to settle (FakeRedisStore is synchronous)

        var list = _store.GetList("run:run-qg-cap:qg");
        list.Count.Should().Be(PipelineConstants.DefaultQualityGateHistoryCapacity,
            $"LTRIM(-{PipelineConstants.DefaultQualityGateHistoryCapacity},-1) must keep exactly the last {PipelineConstants.DefaultQualityGateHistoryCapacity} entries after {overCount} appends");
        // TODO: Also assert (await _sut.GetQualityGateHistoryAsync(runId)).Should().HaveCount(...)
        // to verify that the cap is observable through the public API, not just by inspecting the
        // backing store directly. A deserialization bug in EnqueueDeserialized (e.g. swallowed parse
        // errors) would cause GetQualityGateHistoryAsync to return fewer entries without failing this
        // test. Add the companion assertion when strengthening this coverage.
    }

    // ── RemoveRun hydration ───────────────────────────────────────────────

    [Fact]
    public async Task RemoveRun_HydratesQualityGateHistory_FromRedisList()
    {
        _sut.AddRun(MakeRun("run-qg-hydrate"));

        // Write entries directly to the fake store (avoids fire-and-forget race)
        var r1 = FailedReport();
        var r2 = PassedReport();
        await _store.ListRightPushAsync("run:run-qg-hydrate:qg",
            [JsonSerializer.Serialize(r1), JsonSerializer.Serialize(r2)]);

        var removed = _sut.RemoveRun(new RunId("run-qg-hydrate"));

        removed.Should().NotBeNull();
        removed!.QualityGateHistory.Count.Should().Be(2,
            "RemoveRun must hydrate QualityGateHistory from the Redis list");
        removed.QualityGateHistory.ToArray()[0].Compilation.Passed.Should().BeFalse(
            "first entry must be the failed report");
        removed.QualityGateHistory.ToArray()[1].Compilation.Passed.Should().BeTrue(
            "second entry must be the passed report");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IHubContext<AgentHub> CreateNoOpHubContext()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var context = new Mock<IHubContext<AgentHub>>();
        context.Setup(h => h.Clients).Returns(clients.Object);
        return context.Object;
    }
}
