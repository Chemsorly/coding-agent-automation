using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using Xunit;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentJobRunner"/>.
/// Verifies: execution delegation, OperationCanceledException handling (Cancelled payload),
/// general Exception handling (Failed payload), and RunMode propagation.
/// </summary>
public class AgentJobRunnerTests
{
    private readonly Mock<IPipelineExecutor> _mockExecutor = new();
    private readonly JobAssignmentMessage _assignment;

    public AgentJobRunnerTests()
    {
        _assignment = new JobAssignmentMessage
        {
            JobId = "job-1",
            IssueIdentifier = "owner/repo#42",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#42", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [],
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "test",
            QualityGateConfigs = [],
            LinkedPullRequest = new LinkedPullRequest { BranchName = "feature/x", Url = "http://example.com/pr/1", IsDraft = false, Number = 1 }
        };
    }

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsExecutorResult()
    {
        var expected = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, ct: CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_ReturnsCancelledPayload()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var before = DateTimeOffset.UtcNow;
        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, ct: CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Cancelled);
        result.CompletedAt.Should().BeOnOrAfter(before);
        result.CompletedAt.Should().BeOnOrBefore(before.AddSeconds(10));
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_SetsRunModeFromLinkedPr()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, ct: CancellationToken.None);

        result.RunMode.Should().Be(RunMode.Rework); // LinkedPullRequest is set
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsFailedPayload()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var before = DateTimeOffset.UtcNow;
        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, ct: CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Failed);
        result.FailureReason.Should().Be("Something broke");
        result.CompletedAt.Should().BeOnOrAfter(before);
        result.CompletedAt.Should().BeOnOrBefore(before.AddSeconds(10));
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_SetsRunModeFromLinkedPr()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, ct: CancellationToken.None);

        result.RunMode.Should().Be(RunMode.Rework);
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedPr_RunModeIsNew()
    {
        var assignmentNoPr = _assignment with { LinkedPullRequest = null };

        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, assignmentNoPr, null!,
            _ => { }, CancellationToken.None);

        result.RunMode.Should().Be(RunMode.New);
    }

    [Fact]
    public async Task ExecuteAsync_CallsOnStepChanged()
    {
        PipelineStep? reportedStep = null;
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .Callback<JobAssignmentMessage, HubConnection, OutputBatcher, Action<PipelineStep?>, CancellationToken>(
                (_, _, _, onStep, _) => onStep?.Invoke(PipelineStep.RunningQualityGates))
            .ReturnsAsync(new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow });

        await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            step => reportedStep = step, CancellationToken.None);

        reportedStep.Should().Be(PipelineStep.RunningQualityGates);
    }

    [Fact]
    public async Task ExecuteAsync_PipelineCancelledWithoutSigterm_ReturnsCancelledPayload()
    {
        // Executor returns Cancelled payload (as LocalPipelineExecutor does on pipeline cancel).
        // RethrowOnSigterm is a token that is NOT cancelled — this simulates an operator cancel
        // via CancelJob, where only pipelineCt is cancelled, not the SIGTERM token.
        var cancelledPayload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.Cancelled
        };
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(cancelledPayload);

        using var sigtermCts = new CancellationTokenSource(); // not cancelled
        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, rethrowOnSigterm: sigtermCts.Token);

        result.FinalStep.Should().Be(PipelineStep.Cancelled);
        result.FinalLabel.Should().Be(AgentLabels.Cancelled);
    }

    [Fact]
    public async Task ExecuteAsync_SigtermAfterACompletedPayload_ReturnsThePayload()
    {
        // Executor returns Completed payload. RethrowOnSigterm is already cancelled (SIGTERM arrived
        // after the run finished). A finished run must keep its Completed outcome.
        var completedPayload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedPayload);

        using var sigtermCts = new CancellationTokenSource();
        await sigtermCts.CancelAsync(); // already cancelled when executor returns
        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, rethrowOnSigterm: sigtermCts.Token);

        result.FinalStep.Should().Be(PipelineStep.Completed);
    }
}
