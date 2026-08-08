using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Characterization tests verifying that <see cref="AgentJobLifecycleService"/> correctly
/// clears agent state (ActiveJobId, OrphanRestoredAt, Status → Idle) after each job lifecycle event.
///
/// These tests pin the corrected behavior introduced by issue #1869:
/// - All three handler methods (HandleJobRejectedAsync, HandleJobCompletedAsync,
///   HandleConsolidationRunCompletedAsync) now call _facade.ClearAgentState() which
///   acquires SyncRoot and clears both ActiveJobId and OrphanRestoredAt.
/// - HandleJobRejectedAsync now also clears OrphanRestoredAt (previously omitted).
///
/// Uses a real AgentRegistryService + AgentHubFacade so state mutations are observable.
/// </summary>
public class AgentJobLifecycleServiceClearStateTests
{
    private readonly AgentRegistryService _registry;
    private readonly AgentHubFacade _facade;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILabelService> _mockLabelService;
    private readonly Mock<ILogger> _mockLogger;
    private readonly AgentJobLifecycleService _service;

    public AgentJobLifecycleServiceClearStateTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();
        _mockLabelService = new Mock<ILabelService>();

        var runService = new OrchestratorRunService(_mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);
        var drainService = new JobQueueDrainService(new JobQueueDrainDependencies(
            dispatcher,
            _registry,
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IConsolidationDispatchService>(),
            new ShutdownSignal(),
            _mockLogger.Object));
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockProviderFactory = new Mock<IProviderFactory>();
        var facadeLogger = NullLogger<AgentHubFacadeDependencies>.Instance;

        _facade = new AgentHubFacade(
            _registry,
            runService,
            dispatcher,
            drainService,
            mockHistory.Object,
            mockConfigStore.Object,
            mockProviderFactory.Object,
            facadeLogger);

        var changeNotifier = Mock.Of<IChangeNotifier>();
        var issueOps = new AgentIssueOperations(_facade, _mockLabelService.Object, _mockLogger.Object);

        _service = new AgentJobLifecycleService(
            _facade,
            _mockLifecycleManager.Object,
            _mockLabelService.Object,
            issueOps,
            changeNotifier,
            _mockLogger.Object);
    }

    private AgentEntry RegisterBusyAgent(string agentId = "agent-1")
    {
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-1",
            Labels = ["dotnet"],
            ActiveJob = null
        };
        var entry = _registry.Register(message, connectionId: $"conn-{agentId}");
        _registry.TransitionStatus(agentId, AgentStatus.Busy);
        entry.ActiveJobId = "job-1";
        return entry;
    }

    // ── HandleJobRejectedAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobRejectedAsync_ClearsActiveJobId()
    {
        var agent = RegisterBusyAgent();
        agent.ActiveJobId = "job-1";

        await _service.HandleJobRejectedAsync("job-1", agent, "rejected", CancellationToken.None);

        agent.ActiveJobId.Should().BeNull("HandleJobRejectedAsync must clear ActiveJobId via ClearAgentState");
    }

    [Fact]
    public async Task HandleJobRejectedAsync_ClearsOrphanRestoredAt()
    {
        // This tests the bug fixed in #1869: the old code at L85 did not clear OrphanRestoredAt.
        var agent = RegisterBusyAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        await _service.HandleJobRejectedAsync("job-1", agent, "rejected", CancellationToken.None);

        agent.OrphanRestoredAt.Should().BeNull(
            "HandleJobRejectedAsync must clear OrphanRestoredAt — previously omitted, fixed in #1869");
    }

    [Fact]
    public async Task HandleJobRejectedAsync_TransitionsAgentToIdle()
    {
        var agent = RegisterBusyAgent();

        await _service.HandleJobRejectedAsync("job-1", agent, "rejected", CancellationToken.None);

        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task HandleJobRejectedAsync_SetsLastJobCompletedAt()
    {
        var agent = RegisterBusyAgent();
        var before = DateTimeOffset.UtcNow;

        await _service.HandleJobRejectedAsync("job-1", agent, "rejected", CancellationToken.None);

        agent.LastJobCompletedAt.Should().NotBeNull();
        // TODO: This assertion is weakly constrained — BeOnOrAfter(before) passes for any value
        // set after the test started, including a stale pre-set value with coarse clock resolution.
        // Consider adding .And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5)) to verify
        // the timestamp was actually set during this call, not inherited from a prior state.
        agent.LastJobCompletedAt.Should().BeOnOrAfter(before,
            "LastJobCompletedAt must be set to push agent to back of FIFO queue");
    }

    // ── HandleJobCompletedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task HandleJobCompletedAsync_ClearsActiveJobId()
    {
        var agent = RegisterBusyAgent();
        var run = MakePipelineRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.ActiveJobId.Should().BeNull("HandleJobCompletedAsync must clear ActiveJobId via ClearAgentState");
    }

    [Fact]
    public async Task HandleJobCompletedAsync_ClearsOrphanRestoredAt()
    {
        var agent = RegisterBusyAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var run = MakePipelineRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.OrphanRestoredAt.Should().BeNull("HandleJobCompletedAsync must clear OrphanRestoredAt via ClearAgentState");
    }

    [Fact]
    public async Task HandleJobCompletedAsync_TransitionsAgentToIdle()
    {
        var agent = RegisterBusyAgent();
        var run = MakePipelineRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
        _mockLifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(run);

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.Status.Should().Be(AgentStatus.Idle);
    }

    // ── HandleConsolidationRunCompletedAsync (via HandleJobCompletedAsync branching) ────

    // TODO: The consolidation run tests below do not stub consolidation-service dependencies.
    // They pass only because HandleConsolidationRunCompletedAsync silently swallows service
    // errors or returns early when services return null/default. If consolidation error handling
    // changes (e.g. a service throws instead of returning null), these tests would fail for the
    // wrong reason (unhandled exception rather than assertion failure). Consider adding explicit
    // stubs for consolidation-specific services, or at minimum marking these tests with a comment
    // explaining the assumption so a future failure is easier to diagnose.
    [Fact]
    public async Task HandleJobCompletedAsync_ConsolidationRun_ClearsActiveJobId()
    {
        var agent = RegisterBusyAgent();
        var run = MakeConsolidationRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.ActiveJobId.Should().BeNull(
            "HandleConsolidationRunCompletedAsync must clear ActiveJobId via ClearAgentState");
    }

    [Fact]
    public async Task HandleJobCompletedAsync_ConsolidationRun_ClearsOrphanRestoredAt()
    {
        var agent = RegisterBusyAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var run = MakeConsolidationRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.OrphanRestoredAt.Should().BeNull(
            "HandleConsolidationRunCompletedAsync must clear OrphanRestoredAt via ClearAgentState");
    }

    [Fact]
    public async Task HandleJobCompletedAsync_ConsolidationRun_TransitionsAgentToIdle()
    {
        var agent = RegisterBusyAgent();
        var run = MakeConsolidationRun();
        _facade.AddRun(run);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await _service.HandleJobCompletedAsync("job-1", agent, payload, CancellationToken.None);

        agent.Status.Should().Be(AgentStatus.Idle);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PipelineRun MakePipelineRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    private static PipelineRun MakeConsolidationRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "consolidation/run#1",
        IssueTitle = "Consolidation",
        // Use the consolidation provider config ID so HandleJobCompletedAsync routes to HandleConsolidationRunCompletedAsync
        IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
        RepoProviderConfigId = "repo-cfg-1"
    };
}
