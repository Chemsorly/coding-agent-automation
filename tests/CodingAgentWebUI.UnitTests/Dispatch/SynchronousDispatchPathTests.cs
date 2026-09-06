using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for issue #2322 synchronous dispatch path behavior:
/// - AC#1: Review dispatched before Implementation — Queued=false enables immediate label swap
/// - AC#2: 503/409 → DistributeAndFinalizeAsync returns failure + label reverted to agent:next
/// </summary>
public sealed class SynchronousDispatchPathTests
{
    private readonly Mock<ILabelService> _mockLabelService = new();

    private static JobDistributionRequest MakeRequest(
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        string issueId = "org/repo#42") => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = "ipc-1",
        RepoProviderConfigId = "rpc-1",
        InitiatedBy = "scheduler",
        TaskType = taskType,
        AgentSelector = "dotnet,opencode",
        TimeoutSeconds = 3600,
        RunId = Guid.NewGuid().ToString()
    };

    // ── AC#1: Synchronous path — Queued=false ─────────────────────────────────

    /// <summary>
    /// AC#1: KubernetesWorkDistributor.DistributeAsync always returns Queued=false on the
    /// synchronous dispatch path (issue #2322). This enables DistributeAndFinalizeAsync to
    /// immediately call ConfirmDistributionLabelAsync (label swap to agent:in-progress).
    ///
    /// Priority ordering is enforced by the CALLER (Scheduler) which calls the endpoint
    /// for Review first (higher priority) and Implementation second. Both calls complete
    /// synchronously — no deferred Pending queue.
    /// </summary>
    [Fact]
    public async Task DistributeAsync_ReturnsQueued_False_OnSynchronousPath()
    {
        var workItemId = Guid.NewGuid();
        var mockClient = new Mock<IPipelineApiWorkItemClient>();
        mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workItemId);

        var sut = new KubernetesWorkDistributor(
            mockClient.Object,
            NullLogger<KubernetesWorkDistributor>.Instance);

        var result = await sut.DistributeAsync(MakeRequest(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Queued.Should().BeFalse(
            "the synchronous dispatch path (issue #2322) never queues items as Pending — " +
            "Queued=false triggers immediate label swap in DistributeAndFinalizeAsync");
    }

    /// <summary>
    /// AC#1 (priority ordering): Review WorkItem dispatched before Implementation — both
    /// return Queued=false so label swap to agent:in-progress fires immediately for both
    /// in priority order. The Scheduler dispatches Review first (higher priority).
    /// </summary>
    [Fact]
    public async Task DistributeAsync_ReviewBeforeImpl_BothReturnNonQueued()
    {
        var reviewId = Guid.NewGuid();
        var implId = Guid.NewGuid();
        var callOrder = new List<WorkItemTaskType>();

        var mockClient = new Mock<IPipelineApiWorkItemClient>();
        mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest req, CancellationToken _) =>
            {
                callOrder.Add(req.TaskType);
                return req.TaskType == WorkItemTaskType.Review ? reviewId : implId;
            });

        var sut = new KubernetesWorkDistributor(
            mockClient.Object,
            NullLogger<KubernetesWorkDistributor>.Instance);

        // Simulate Scheduler calling Review first (higher priority), Implementation second
        var reviewResult = await sut.DistributeAsync(
            MakeRequest(WorkItemTaskType.Review, "org/repo#1"),
            CancellationToken.None);
        var implResult = await sut.DistributeAsync(
            MakeRequest(WorkItemTaskType.Implementation, "org/repo#2"),
            CancellationToken.None);

        reviewResult.Success.Should().BeTrue();
        reviewResult.Queued.Should().BeFalse("synchronous path always returns Queued=false");
        reviewResult.WorkItemId.Should().Be(reviewId.ToString());
        implResult.Success.Should().BeTrue();
        implResult.Queued.Should().BeFalse("synchronous path always returns Queued=false");
        implResult.WorkItemId.Should().Be(implId.ToString());

        callOrder.Should().Equal(
            new[] { WorkItemTaskType.Review, WorkItemTaskType.Implementation },
            "the Scheduler's priority ordering is preserved because dispatch is synchronous — " +
            "no Pending queue reorders items");
    }

    // ── AC#2: 503/409 → DistributeAndFinalizeAsync returns failure + label reverted ──────

    /// <summary>
    /// AC#2a: When DispatchAsync throws DispatchNoCapacityException (503 — no PVC/K8s failure),
    /// DistributeAsync returns DistributionResult(Success=false), and DistributeAndFinalizeAsync
    /// calls RevertFailedDistributionAsync, which swaps the GitHub label back to agent:next.
    /// </summary>
    [Fact]
    public async Task DistributeAndFinalizeAsync_When503_RevertsLabelToNext()
    {
        var mockClient = new Mock<IPipelineApiWorkItemClient>();
        mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DispatchNoCapacityException(
                System.Net.HttpStatusCode.ServiceUnavailable,
                "No PVC available in the kiro pool"));

        var distributor = new KubernetesWorkDistributor(
            mockClient.Object,
            NullLogger<KubernetesWorkDistributor>.Instance);

        var service = BuildDispatchOrchestrationService(distributor);
        var request = MakeRequest();

        var outcome = await service.DistributeAndFinalizeAsync(request, CancellationToken.None);

        outcome.Success.Should().BeFalse("503 from dispatch endpoint means no capacity");
        outcome.ErrorMessage.Should().NotBeNullOrEmpty();
        outcome.Queued.Should().BeFalse();

        // Label must be reverted to agent:next so the Scheduler re-queues the item
        _mockLabelService.Verify(
            s => s.SwapLabelAsync(
                request.IssueProviderConfigId,
                request.IssueIdentifier,
                AgentLabels.Next,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "RevertFailedDistributionAsync must swap label back to agent:next on 503 " +
            "so the issue re-enters the Scheduler queue on the next poll cycle");

        // agent:in-progress must NOT be set (job was never dispatched)
        _mockLabelService.Verify(
            s => s.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                AgentLabels.InProgress,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "label must not be swapped to agent:in-progress when dispatch fails");
    }

    /// <summary>
    /// AC#2b: When DispatchAsync throws DispatchNoCapacityException (409 — concurrency limit),
    /// the same behavior applies: label is reverted to agent:next.
    /// </summary>
    [Fact]
    public async Task DistributeAndFinalizeAsync_When409_RevertsLabelToNext()
    {
        var mockClient = new Mock<IPipelineApiWorkItemClient>();
        mockClient
            .Setup(c => c.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DispatchNoCapacityException(
                System.Net.HttpStatusCode.Conflict,
                "Concurrency limit 2 reached for selector 'dotnet,kiro'"));

        var distributor = new KubernetesWorkDistributor(
            mockClient.Object,
            NullLogger<KubernetesWorkDistributor>.Instance);

        var service = BuildDispatchOrchestrationService(distributor);
        var request = MakeRequest();

        var outcome = await service.DistributeAndFinalizeAsync(request, CancellationToken.None);

        outcome.Success.Should().BeFalse("409 (concurrency limit) means no capacity");

        _mockLabelService.Verify(
            s => s.SwapLabelAsync(
                request.IssueProviderConfigId,
                request.IssueIdentifier,
                AgentLabels.Next,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "label must be reverted to agent:next (409 = no capacity, not a permanent failure)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private DispatchOrchestrationService BuildDispatchOrchestrationService(IWorkDistributor distributor)
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var mockProviderFactory = new Mock<IProviderFactory>();
        var mockTokenVending = new Mock<ITokenVendingService>();
        var mockLogger = new Mock<ILogger>();

        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            mockConfigStore.Object,
            mockLogger.Object);

        return new DispatchOrchestrationService(
            new DispatchOrchestrationServiceDependencies(
                new DispatchInfrastructure(
                    mockTokenVending.Object,
                    mockProviderFactory.Object,
                    _mockLabelService.Object,
                    resolution),
                distributor,
                mockConfigStore.Object,
                mockConfigStore.Object,
                mockConfigStore.Object),
            mockLogger.Object);
    }
}
