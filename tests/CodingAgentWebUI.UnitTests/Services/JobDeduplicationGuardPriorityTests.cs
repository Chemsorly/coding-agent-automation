using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for priority-based dequeue behaviour in <see cref="JobDeduplicationGuardService"/>.
/// Verifies that Review > Decomposition > Implementation > Consolidation dispatch order is respected,
/// that within-tier FIFO ordering is preserved, and that label compatibility is still enforced.
/// </summary>
public class JobDeduplicationGuardPriorityTests
{
    private static JobDeduplicationGuardService CreateService() =>
        new(new AgentRegistryService(new Mock<ILogger>().Object), new Mock<ILogger>().Object);

    private static AgentEntry CreateAgent(IReadOnlyList<string>? labels = null) => new()
    {
        AgentId = "agent-1",
        ConnectionId = "conn-1",
        Hostname = "host",
        Labels = labels ?? new[] { "dotnet" },
        RegisteredAt = DateTimeOffset.UtcNow,
        LastHeartbeatAt = DateTimeOffset.UtcNow
    };

    private static PendingJob CreateJob(
        string issueId,
        PipelineRunType runType = PipelineRunType.Implementation,
        IReadOnlyList<string>? labels = null,
        DateTimeOffset? enqueuedAt = null,
        bool isConsolidation = false) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderId = isConsolidation ? "consolidation" : "ip",
        RepoProviderId = "rp",
        EnqueuedAt = enqueuedAt ?? DateTimeOffset.UtcNow,
        InitiatedBy = "test",
        RequiredLabels = labels ?? Array.Empty<string>(),
        RunType = runType,
        TaskType = isConsolidation ? WorkItemTaskType.Consolidation : WorkItemTaskType.Implementation,
        ConsolidationRunType = isConsolidation ? CodingAgentWebUI.Pipeline.Models.ConsolidationRunType.BrainConsolidation : null
    };

    /// <summary>
    /// When an Implementation job is enqueued before a Review job, dequeue must return
    /// the Review job first (higher priority).
    /// </summary>
    [Fact]
    public void DequeueForAgent_ReviewBeforeImplementation_ReviewDispatchedFirst()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("review-1", PipelineRunType.Review, enqueuedAt: t0.AddSeconds(1)));

        var job = service.DequeueForAgent(agent);

        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be("review-1", "Review has higher priority than Implementation");
        job.RunType.Should().Be(PipelineRunType.Review);
    }

    /// <summary>
    /// When an Implementation job is enqueued before a DecompositionAnalysis job, dequeue must
    /// return the DecompositionAnalysis job first (higher priority).
    /// </summary>
    [Fact]
    public void DequeueForAgent_DecompositionBeforeImplementation_DecompositionDispatchedFirst()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("decomp-1", PipelineRunType.DecompositionAnalysis, enqueuedAt: t0.AddSeconds(1)));

        var job = service.DequeueForAgent(agent);

        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be("decomp-1", "DecompositionAnalysis has higher priority than Implementation");
        job.RunType.Should().Be(PipelineRunType.DecompositionAnalysis);
        // TODO: add a parallel test using PipelineRunType.Decomposition (not DecompositionAnalysis)
        // vs. Implementation to confirm both Decomposition enum values share priority=1. If a future
        // refactor accidentally assigns PipelineRunType.Decomposition a different priority, the current
        // test (which only covers DecompositionAnalysis) would not catch the regression.
    }

    /// <summary>
    /// When two Implementation jobs are queued, FIFO is preserved (first enqueued is returned first).
    /// </summary>
    [Fact]
    public void DequeueForAgent_TwoImplementationJobs_FIFOPreserved()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("impl-2", PipelineRunType.Implementation, enqueuedAt: t0.AddSeconds(1)));

        var first = service.DequeueForAgent(agent);
        first!.IssueIdentifier.Should().Be("impl-1", "FIFO: first enqueued should be returned first within the same priority tier");

        var second = service.DequeueForAgent(agent);
        second!.IssueIdentifier.Should().Be("impl-2");
        // TODO: assert service.QueueLength == 0 here to catch bugs that silently re-enqueue a job
        // and leave the queue non-empty after both dequeues.
    }

    /// <summary>
    /// A Consolidation job enqueued before an Implementation job must be dispatched last
    /// (Consolidation has lowest priority).
    /// </summary>
    [Fact]
    public void DequeueForAgent_ConsolidationLastAmongCompatible()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        service.EnqueueJob(CreateJob("consol-1", isConsolidation: true, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0.AddSeconds(1)));

        var first = service.DequeueForAgent(agent);
        first!.IssueIdentifier.Should().Be("impl-1", "Implementation has higher priority than Consolidation");

        var second = service.DequeueForAgent(agent);
        second!.IssueIdentifier.Should().Be("consol-1");
        // TODO: add a variant of this test that also queues Review and Decomposition jobs alongside
        // Consolidation, to guard against a regression that accidentally assigns Consolidation the
        // same priority as Implementation (priority=2). The current test only covers Consolidation
        // vs. one Implementation job, so a priority=2 Consolidation bug would go undetected.
    }

    /// <summary>
    /// With all run types enqueued (Consolidation, Implementation, DecompositionAnalysis, Decomposition,
    /// Review), successive dequeues must respect the priority order:
    /// Review → Decomposition/DecompositionAnalysis → Implementation → Consolidation.
    /// </summary>
    [Fact]
    public void DequeueForAgent_AllTypesQueued_PriorityOrderRespected()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        // Enqueue in worst-case order (lowest priority first)
        service.EnqueueJob(CreateJob("consol-1", isConsolidation: true, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0.AddSeconds(1)));
        service.EnqueueJob(CreateJob("decomp-analysis-1", PipelineRunType.DecompositionAnalysis, enqueuedAt: t0.AddSeconds(2)));
        service.EnqueueJob(CreateJob("decomp-1", PipelineRunType.Decomposition, enqueuedAt: t0.AddSeconds(3)));
        service.EnqueueJob(CreateJob("review-1", PipelineRunType.Review, enqueuedAt: t0.AddSeconds(4)));
        // TODO: The two Decomposition-tier jobs above have monotonically increasing EnqueuedAt
        // (t0+2s and t0+3s), so the tie-break on EnqueuedAt is never independently stressed
        // within the Decomposition priority tier. A reversed tie-break (using > instead of <)
        // would still pass this test because decomp-analysis-1 wins by EnqueuedAt. Add a test
        // variant where two same-priority Decomposition-tier jobs have the same priority value
        // and differ only in EnqueuedAt, to isolate the tie-break logic for that tier.

        // 1st dequeue: Review (priority 0)
        var job1 = service.DequeueForAgent(agent);
        job1!.IssueIdentifier.Should().Be("review-1");

        // 2nd dequeue: DecompositionAnalysis (priority 1, earlier EnqueuedAt than Decomposition)
        var job2 = service.DequeueForAgent(agent);
        job2!.IssueIdentifier.Should().Be("decomp-analysis-1");

        // 3rd dequeue: Decomposition (priority 1)
        var job3 = service.DequeueForAgent(agent);
        job3!.IssueIdentifier.Should().Be("decomp-1");

        // 4th dequeue: Implementation (priority 2)
        var job4 = service.DequeueForAgent(agent);
        job4!.IssueIdentifier.Should().Be("impl-1");

        // 5th dequeue: Consolidation (priority 3)
        var job5 = service.DequeueForAgent(agent);
        job5!.IssueIdentifier.Should().Be("consol-1");

        service.QueueLength.Should().Be(0);
    }

    /// <summary>
    /// A high-priority Review job that requires labels the agent doesn't have must be skipped,
    /// and the lower-priority but label-compatible Implementation job must be returned.
    /// Label compatibility is enforced independently of priority.
    /// </summary>
    [Fact]
    public void DequeueForAgent_IncompatibleHighPriorityJob_SkippedInFavorOfCompatibleLowerPriority()
    {
        var service = CreateService();
        // Agent only has "dotnet" — cannot run the "review-agent" job
        var agent = CreateAgent(labels: new[] { "dotnet" });

        var t0 = DateTimeOffset.UtcNow;
        service.EnqueueJob(CreateJob("review-1", PipelineRunType.Review,
            labels: new[] { "review-agent" }, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation,
            labels: new[] { "dotnet" }, enqueuedAt: t0.AddSeconds(1)));

        var job = service.DequeueForAgent(agent);

        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be("impl-1",
            "label-incompatible Review job must be skipped; compatible Implementation job must be returned");

        // The Review job must remain in the queue
        service.QueueLength.Should().Be(1);
    }

    /// <summary>
    /// After a high-priority job is selected and removed, the remaining lower-priority jobs must be
    /// re-enqueued in their original relative order (FIFO within tier preserved for subsequent calls).
    /// </summary>
    [Fact]
    public void DequeueForAgent_RemainderReenqueuedInOriginalOrder_FIFOWithinTierPreserved()
    {
        var service = CreateService();
        var agent = CreateAgent();

        var t0 = DateTimeOffset.UtcNow;
        // Enqueue three Implementation jobs in FIFO order, then a Review job
        service.EnqueueJob(CreateJob("impl-1", PipelineRunType.Implementation, enqueuedAt: t0));
        service.EnqueueJob(CreateJob("impl-2", PipelineRunType.Implementation, enqueuedAt: t0.AddSeconds(1)));
        service.EnqueueJob(CreateJob("impl-3", PipelineRunType.Implementation, enqueuedAt: t0.AddSeconds(2)));
        service.EnqueueJob(CreateJob("review-1", PipelineRunType.Review, enqueuedAt: t0.AddSeconds(3)));

        // First dequeue: Review is selected (highest priority), impl-1/2/3 re-enqueued in original order
        var first = service.DequeueForAgent(agent);
        first!.IssueIdentifier.Should().Be("review-1");

        // Second dequeue: impl-1 (first of the remaining Implementation jobs — FIFO preserved)
        var second = service.DequeueForAgent(agent);
        second!.IssueIdentifier.Should().Be("impl-1", "FIFO within tier must be preserved after re-enqueue");

        var third = service.DequeueForAgent(agent);
        third!.IssueIdentifier.Should().Be("impl-2");

        var fourth = service.DequeueForAgent(agent);
        fourth!.IssueIdentifier.Should().Be("impl-3");
    }
}
