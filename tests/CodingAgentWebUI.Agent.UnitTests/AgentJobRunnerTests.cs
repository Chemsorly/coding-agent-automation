using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentJobRunner"/>.
/// Verifies: execution delegation, OperationCanceledException handling (Cancelled payload),
/// general Exception handling (Failed payload), and IsRework propagation.
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
            _ => { }, CancellationToken.None);

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

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Cancelled);
        result.CompletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_SetsIsReworkFromLinkedPr()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, CancellationToken.None);

        result.IsRework.Should().BeTrue(); // LinkedPullRequest is set
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsFailedPayload()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Failed);
        result.FailureReason.Should().Be("Something broke");
        result.CompletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_SetsIsReworkFromLinkedPr()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(), It.IsAny<HubConnection>(),
            It.IsAny<OutputBatcher>(), It.IsAny<Action<PipelineStep?>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fail"));

        var result = await AgentJobRunner.ExecuteAsync(
            _mockExecutor.Object, _assignment, null!,
            _ => { }, CancellationToken.None);

        result.IsRework.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedPr_IsReworkIsFalse()
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

        result.IsRework.Should().BeFalse();
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
}
