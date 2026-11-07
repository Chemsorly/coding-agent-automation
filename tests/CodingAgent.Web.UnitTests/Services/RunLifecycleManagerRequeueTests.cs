using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Spec 056 DD11 step 3, req 6.3 (task 6.2): a run whose credential could not be used ends with a history row, its queue
/// label back (never <c>agent:error</c>) and its Job deleted — also when the in-memory run is already gone.
/// </summary>
public sealed class RunLifecycleManagerRequeueTests
{
    private static readonly string[] DotnetLabels = ["dotnet"];

    private readonly Mock<ILogger> _logger = new();
    private readonly Mock<ILabelService> _labels = new();
    private readonly Mock<IPipelineRunHistoryService> _history = new();
    private readonly Mock<IJobCleanupStrategy> _jobs = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runs;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerRequeueTests()
    {
        _registry = new AgentRegistryService(_logger.Object);
        _runs = new OrchestratorRunService(_logger.Object);
        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runs, _history.Object, _registry, _labels.Object, _logger.Object, _jobs.Object));
    }

    private static RequeueTarget Target(PipelineRunType runType, string? agentId = "agent-1") =>
        new(runType, "org/repo#7", "ip-1", "rp-1", agentId);

    private PipelineRun AddRun(string runId, PipelineRunType runType)
    {
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#7",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = runType,
            AgentId = "agent-1",
        };
        _runs.AddRun(run);
        return run;
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation, AgentLabels.Next)]
    [InlineData(PipelineRunType.DecompositionAnalysis, AgentLabels.Epic)]
    [InlineData(PipelineRunType.Decomposition, AgentLabels.EpicApproved)]
    [InlineData(PipelineRunType.Triage, AgentLabels.Triage)]
    public async Task WhenRunIsKnown_ShouldPutTheIssueBackInItsQueue_AndWriteAHistoryRow(PipelineRunType runType, string label)
    {
        AddRun("run-1", runType);

        var ended = await _sut.RequeueRunAsync(new RunId("run-1"), "Requeued: Kiro paused — quota", Target(runType), CancellationToken.None);

        ended.Should().NotBeNull();
        _runs.GetRun(new RunId("run-1")).Should().BeNull();
        _labels.Verify(l => l.SwapLabelAsync("ip-1", "org/repo#7", label, LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
        _labels.Verify(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
        _history.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.FailureCategory == FailureReason.ProviderUnavailable
                && r.CurrentStep == PipelineStep.Failed
                && r.FailureReason == "Requeued: Kiro paused — quota"),
            It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(j => j.TryDeleteJobForRunAsync(new RunId("run-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenReviewRunIsKnown_ShouldPutThePullRequestBackInTheQueue()
    {
        AddRun("run-r", PipelineRunType.Review);

        await _sut.RequeueRunAsync(new RunId("run-r"), "Requeued", Target(PipelineRunType.Review), CancellationToken.None);

        _labels.Verify(l => l.SwapLabelAsync("rp-1", "org/repo#7", AgentLabels.Next, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenConsolidation_ShouldSwapNoLabel()
    {
        AddRun("run-c", PipelineRunType.Consolidation);

        await _sut.RequeueRunAsync(new RunId("run-c"), "Requeued", Target(PipelineRunType.Consolidation), CancellationToken.None);

        _labels.Verify(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(j => j.TryDeleteJobForRunAsync(new RunId("run-c"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation, "ip-1", AgentLabels.Next, LabelTargetKind.Issue)]
    [InlineData(PipelineRunType.Triage, "ip-1", AgentLabels.Triage, LabelTargetKind.Issue)]
    [InlineData(PipelineRunType.Review, "rp-1", AgentLabels.Next, LabelTargetKind.PullRequest)]
    public async Task WhenRunIsMissingFromTheStore_ShouldStillSwapTheLabelAndDeleteTheJob_WithoutAHistoryRow(
        PipelineRunType runType, string provider, string label, LabelTargetKind kind)
    {
        var ended = await _sut.RequeueRunAsync(new RunId("run-gone"), "Requeued", Target(runType), CancellationToken.None);

        ended.Should().BeNull();
        _labels.Verify(l => l.SwapLabelAsync(provider, "org/repo#7", label, kind, It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(j => j.TryDeleteJobForRunAsync(new RunId("run-gone"), It.IsAny<CancellationToken>()), Times.Once);
        _history.Verify(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenAgentIsBusyOnTheRun_ShouldClearItsActiveJob()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-agent-1",
            Labels = DotnetLabels
        }, "conn-agent-1");
        agent.ActiveJobId = "run-b";
        AddRun("run-b", PipelineRunType.Implementation);

        await _sut.RequeueRunAsync(new RunId("run-b"), "Requeued", Target(PipelineRunType.Implementation), CancellationToken.None);

        _registry.GetByAgentId("agent-1")!.ActiveJobId.Should().BeNull();
    }
}
