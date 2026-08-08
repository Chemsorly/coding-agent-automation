using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="OrphanedRunSweepPhase"/> in isolation.
/// </summary>
public class OrphanedRunSweepPhaseTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILogger> _mockLogger;
    private readonly OrphanedRunSweepPhase _phase;

    public OrphanedRunSweepPhaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();

        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(new PipelineRun
            {
                RunId = "run-1", IssueIdentifier = "test/repo#0", IssueTitle = "Test",
                IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            });

        _phase = new OrphanedRunSweepPhase(_registry, _runService, _mockLifecycleManager.Object, _mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static PipelineRun MakeRun(string runId, string? agentId)
        => new()
        {
            RunId = runId,
            AgentId = agentId,
            IssueIdentifier = $"org/repo#{runId}",
            IssueTitle = "Test run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_NoActiveRuns_NoFailRunCalled()
    {
        await _phase.ExecuteAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_RunWithNullAgentId_NotOrphaned()
    {
        _runService.AddRun(MakeRun("run-1", agentId: null));

        await _phase.ExecuteAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never,
            "run with null AgentId must not be treated as orphaned");
    }

    [Fact]
    public async Task Execute_RunWhoseAgentIsInRegistry_NotOrphaned()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-alive", Hostname = "host-1", Labels = [], ActiveJob = null
        }, "conn-1");
        _runService.AddRun(MakeRun("run-1", agentId: "agent-alive"));

        await _phase.ExecuteAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()),
            Times.Never,
            "run whose agent is still registered must not be orphaned");
    }

    [Fact]
    public async Task Execute_RunWhoseAgentIsGoneFromRegistry_OrphanedAndFailed()
    {
        // Agent not in registry — orphaned run
        _runService.AddRun(MakeRun("run-1", agentId: "agent-gone"));

        await _phase.ExecuteAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync("run-1", "Agent deregistered (orphaned run)", It.IsAny<CancellationToken>(), FailureReason.InfrastructureFailure),
            Times.Once);
    }

    [Fact]
    public async Task Execute_OrphanedRun_WarningLoggedWithRunIdIssueIdentifierAndAgentId()
    {
        _runService.AddRun(new PipelineRun
        {
            RunId = "run-orphan",
            AgentId = "agent-gone",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        });

        await _phase.ExecuteAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // The warning template contains {RunId}, {IssueIdentifier}, {AgentId}.
        // IssueIdentifier is a struct, not string — use It.IsAny<IssueIdentifier>().
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{RunId}") && s.Contains("{IssueIdentifier}") && s.Contains("{AgentId}")),
                It.IsAny<string>(),       // RunId
                It.IsAny<IssueIdentifier>(), // IssueIdentifier
                It.IsAny<string>()),      // AgentId
            Times.Once);
    }

    public void Dispose() { }
}
