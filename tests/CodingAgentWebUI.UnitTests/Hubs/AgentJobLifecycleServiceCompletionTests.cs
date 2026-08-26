using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Characterization tests for the three completion handler paths inside
/// <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/>.
///
/// Written BEFORE the CompletionOutcomeResolver extraction to lock in existing behaviour
/// and prevent regression. These tests assert observable side-effects (calls to mocked
/// dependencies), not internal state.
///
/// Note: MarkIssueComplete was removed from IAgentHubFacade in T18 (arch-audit 2026-08-22)
/// as part of deleting the dead in-memory dedup queue. Test cases that only verified the
/// MarkIssueComplete call have been removed; tests that tested other behaviour alongside it
/// have had those specific Verify calls stripped.
/// </summary>
public sealed class AgentJobLifecycleServiceCompletionTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycleManager = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentJobLifecycleService CreateService() => new(
        _facade.Object,
        _lifecycleManager.Object,
        _labelService.Object,
        _issueOps.Object,
        _changeNotifier.Object,
        _logger.Object);

    private static PipelineRun MakeRun(string jobId = "job-1", string? providerConfigId = null) => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = providerConfigId ?? "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        AgentProviderConfigId = "agent-cfg-1"
    };

    private static PipelineRun MakeConsolidationRun(string jobId = "job-1") =>
        MakeRun(jobId, ConsolidationConstants.ProviderConfigId);

    private static AgentEntry MakeAgent(string agentId = "agent-1") => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = "job-1"
    };

    // ── Consolidation path ────────────────────────────────────────────────────

    [Fact]
    public async Task Consolidation_completed_step_transitions_WorkItem_to_Succeeded()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Consolidation_failed_step_transitions_WorkItem_to_Failed_with_reason()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "out of tokens",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "out of tokens", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Consolidation_failed_step_with_null_reason_uses_fallback()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Consolidation run failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Consolidation_cancelled_step_transitions_WorkItem_to_Cancelled()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Consolidation_transitions_agent_to_Idle()
    {
        var run = MakeConsolidationRun();
        var agent = MakeAgent();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), agent, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task Consolidation_removes_run()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        var svc = CreateService();

        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
    }

    // ── Regular path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Regular_completed_step_calls_CompleteRunAsync_with_Succeeded()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Regular_failed_step_calls_CompleteRunAsync_with_Failed_and_reason()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "build failed",
            FailureCategory = FailureReason.AgentError
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                "build failed", FailureReason.AgentError))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _lifecycleManager.Verify(l => l.CompleteRunAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "build failed", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Regular_null_CompletedRun_falls_back_to_direct_TransitionWorkItemAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Regular_CompleteRunAsync_throws_invokes_defensive_cleanup_with_defensive_fallback_string()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (defensive cleanup after exception)",
            FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Defensive_cleanup_removes_run()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ThrowsAsync(new InvalidOperationException("DB failure"));

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.RemoveRun("job-1"), Times.Once);
    }

    // ── Orphaned path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Orphaned_completed_step_transitions_WorkItem_to_Succeeded_and_attempts_label_swap()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null), Times.Once);
    }

    [Fact]
    public async Task Orphaned_failed_step_transitions_WorkItem_to_Failed_with_fallback()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            FailureCategory = null
        };
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "Agent reported failure (run not in memory)", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Orphaned_cancelled_step_transitions_WorkItem_to_Cancelled_without_label_swap()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Cancelled, It.IsAny<CancellationToken>(), null, null), Times.Once);
        _facade.Verify(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Post-completion bookkeeping contract ──────────────────────────────────

    [Fact]
    public async Task Regular_completed_step_calls_PostIssueFeedbackCommentAsync()
    {
        var run = MakeRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(i => i.PostIssueFeedbackCommentAsync(run), Times.Once);
    }

    [Fact]
    public async Task Consolidation_does_not_call_PostIssueFeedbackCommentAsync()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(i => i.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task Consolidation_does_not_call_SwapLabelAsync()
    {
        var run = MakeConsolidationRun();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _issueOps.Verify(i => i.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Orphaned_failed_step_with_explicit_reason_propagates_reason_to_TransitionWorkItem()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "explicit agent error message",
            FailureCategory = FailureReason.AgentError
        };

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _facade.Verify(f => f.TransitionWorkItemAsync(
            "job-1", WorkItemStatus.Failed, It.IsAny<CancellationToken>(),
            "explicit agent error message", FailureReason.AgentError), Times.Once);
    }

    [Fact]
    public async Task Orphaned_completed_step_calls_LabelService_SwapLabel_with_Done_when_metadata_available()
    {
        _facade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetWorkItemIssueMetadataAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#3", "prov-cfg-1"));

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), null, payload, CancellationToken.None);

        _labelService.Verify(l => l.SwapLabelAsync(
            "prov-cfg-1", "org/repo#3",
            AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Regular_completed_clears_agent_OrphanRestoredAt_and_sets_LastJobCompletedAt()
    {
        var run = MakeRun();
        var agent = MakeAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _lifecycleManager
            .Setup(l => l.CompleteRunAsync("job-1", WorkItemStatus.Succeeded,
                It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(run);

        var svc = CreateService();
        await svc.HandleJobCompletedAsync(new JobId("job-1"), agent, payload, CancellationToken.None);

        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
        agent.LastJobCompletedAt.Should().BeAfter(before);
        _facade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
    }
}
