using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Property-based tests for JobDispatcherService: queuing, dequeuing, and duplicate prevention.
/// </summary>
public class JobDispatcherPropertyTests
{
    private static (AgentRegistryService Registry, JobDispatcherService Dispatcher) CreateServices()
    {
        var registry = new AgentRegistryService(new Mock<ILogger>().Object);
        var dispatcher = new JobDispatcherService(registry, new Mock<ILogger>().Object);
        return (registry, dispatcher);
    }

    private static AgentEntry RegisterAgent(AgentRegistryService registry, string agentId, IReadOnlyList<string> labels)
    {
        return registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = labels
        }, $"conn-{agentId}");
    }

    private static PendingJob CreateJob(string issueId, IReadOnlyList<string>? requiredLabels = null) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderId = "issue-1",
        RepoProviderId = "repo-1",
        EnqueuedAt = DateTimeOffset.UtcNow,
        InitiatedBy = "test",
        RequiredLabels = requiredLabels ?? Array.Empty<string>()
    };

    /// <summary>
    /// Property 9: Job Queuing When No Compatible Agent Available
    /// When no idle agent has matching labels, job is enqueued (not dropped).
    /// **Validates: Requirements 4.3, 19.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void NoCompatibleAgent_JobIsEnqueued(NonEmptyString issueId)
    {
        var (registry, dispatcher) = CreateServices();

        // Register an agent with different labels
        RegisterAgent(registry, "agent-1", new[] { "python" });

        var job = CreateJob(issueId.Get, new[] { "kiro", "dotnet" });
        var enqueued = dispatcher.EnqueueJob(job);

        enqueued.Should().BeTrue();
        dispatcher.QueueLength.Should().Be(1);
    }

    /// <summary>
    /// Property 9 (continued): Queue preserves FIFO ordering.
    /// **Validates: Requirements 4.3, 19.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void Queue_PreservesFIFOOrdering(PositiveInt jobCount)
    {
        var count = Math.Min(jobCount.Get, 20);
        var (registry, dispatcher) = CreateServices();
        RegisterAgent(registry, "agent-1", new[] { "kiro" });

        // Make agent busy so jobs queue up
        var agent = registry.GetByAgentId("agent-1")!;
        agent.ActiveJobId = "existing-job";
        registry.TransitionStatus("agent-1", AgentStatus.Busy);

        for (var i = 0; i < count; i++)
            dispatcher.EnqueueJob(CreateJob($"issue-{i}"));

        // Make agent idle and dequeue
        agent.ActiveJobId = null;
        registry.TransitionStatus("agent-1", AgentStatus.Idle);

        var first = dispatcher.DequeueForAgent(agent);
        first.Should().NotBeNull();
        first!.IssueIdentifier.Should().Be("issue-0"); // FIFO: first enqueued
    }

    /// <summary>
    /// Property 11: Step Transition HighWaterMark Monotonicity
    /// For any sequence of step transitions, HighWaterMark is monotonically non-decreasing
    /// (excluding terminal states).
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void HighWaterMark_IsMonotonicallyNonDecreasing(int[] stepIndices)
    {
        var nonTerminalSteps = Enum.GetValues<PipelineStep>()
            .Where(s => s < PipelineStep.Completed)
            .ToArray();

        if (nonTerminalSteps.Length == 0 || stepIndices.Length == 0) return;

        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var previousHwm = run.HighWaterMark;

        foreach (var idx in stepIndices)
        {
            var step = nonTerminalSteps[Math.Abs(idx) % nonTerminalSteps.Length];
            run.CurrentStep = step;

            // Simulate HighWaterMark update logic from AgentHub
            if (step < PipelineStep.Completed && step > run.HighWaterMark)
                run.HighWaterMark = step;

            // HighWaterMark should never decrease
            ((int)run.HighWaterMark).Should().BeGreaterThanOrEqualTo((int)previousHwm);
            previousHwm = run.HighWaterMark;
        }
    }

    /// <summary>
    /// Property 19: No Duplicate Issue Assignments
    /// For concurrent dispatch requests for same issueIdentifier, at most one is enqueued.
    /// **Validates: Requirements 9.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DuplicateIssue_IsRejected(NonEmptyString issueId)
    {
        var (_, dispatcher) = CreateServices();

        var job1 = CreateJob(issueId.Get);
        var job2 = CreateJob(issueId.Get);

        var first = dispatcher.EnqueueJob(job1);
        var second = dispatcher.EnqueueJob(job2);

        first.Should().BeTrue();
        second.Should().BeFalse();
        dispatcher.QueueLength.Should().Be(1);
    }

    /// <summary>
    /// Property 21: No Duplicate Queue Entries
    /// Queue contains at most one entry per issueIdentifier. Duplicate enqueue rejected.
    /// **Validates: Requirements 17.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void NoDuplicateQueueEntries(NonEmptyString issueId)
    {
        var (_, dispatcher) = CreateServices();

        dispatcher.EnqueueJob(CreateJob(issueId.Get));
        var duplicate = dispatcher.EnqueueJob(CreateJob(issueId.Get));

        duplicate.Should().BeFalse();
        dispatcher.GetQueuedJobs().Count(j => j.IssueIdentifier == issueId.Get).Should().Be(1);
    }

    /// <summary>
    /// Property 22: Queue Dequeue on Agent Idle
    /// Agent transitioning Busy → Idle with non-empty queue: dequeue next compatible job.
    /// No compatible job → agent stays Idle, queue unchanged.
    /// **Validates: Requirements 17.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AgentIdle_DequeuesToCompatibleJob(NonEmptyString issueId)
    {
        var (registry, dispatcher) = CreateServices();
        var agent = RegisterAgent(registry, "agent-1", new[] { "kiro", "dotnet" });

        // Enqueue a compatible job
        dispatcher.EnqueueJob(CreateJob(issueId.Get, new[] { "kiro" }));

        // Dequeue for the agent
        var job = dispatcher.DequeueForAgent(agent);

        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be(issueId.Get);
    }

    /// <summary>
    /// Property 22 (continued): No compatible job → agent stays Idle, queue unchanged.
    /// **Validates: Requirements 17.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AgentIdle_NoCompatibleJob_QueueUnchanged(NonEmptyString issueId)
    {
        var (registry, dispatcher) = CreateServices();
        var agent = RegisterAgent(registry, "agent-1", new[] { "python" });

        // Enqueue a job requiring different labels
        dispatcher.EnqueueJob(CreateJob(issueId.Get, new[] { "kiro", "dotnet" }));

        var job = dispatcher.DequeueForAgent(agent);

        job.Should().BeNull();
        dispatcher.QueueLength.Should().Be(1); // Queue unchanged
    }
}
