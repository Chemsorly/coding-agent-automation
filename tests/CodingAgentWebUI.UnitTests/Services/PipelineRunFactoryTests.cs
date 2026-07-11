using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PipelineRunFactory.FromDistributionRequest"/>.
/// </summary>
public sealed class PipelineRunFactoryTests
{
    [Fact]
    public void FromDistributionRequest_Creates_PipelineRun_WithAllFields()
    {
        // Arrange
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600,
            RunId = "run-abc-123",
            RunType = PipelineRunType.Review,
            IssueDetail = new IssueDetail
            {
                Identifier = "owner/repo#42",
                Title = "My Issue Title",
                Description = "desc",
                Labels = ["bug"]
            }
        };

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request, "agent-7");

        // Assert
        run.RunId.Should().Be("run-abc-123");
        run.IssueIdentifier.Should().Be("owner/repo#42");
        run.IssueTitle.Should().Be("My Issue Title");
        run.IssueProviderConfigId.Should().Be("ip-1");
        run.RepoProviderConfigId.Should().Be("rp-1");
        run.RunType.Should().Be(PipelineRunType.Review);
        run.InitiatedBy.Should().Be("manual");
        run.AgentId.Should().Be("agent-7");
    }

    [Fact]
    public void FromDistributionRequest_FallsBackToIssueIdentifier_WhenIssueDetailIsNull()
    {
        // Arrange
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#99",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = "run-no-detail",
            IssueDetail = null
        };

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request);

        // Assert
        run.IssueTitle.Should().Be("owner/repo#99");
    }

    [Fact]
    public void FromDistributionRequest_DefaultsInitiatedBy_WhenNull()
    {
        // Arrange
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#5",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = null!,
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 1800,
            RunId = "run-no-initiator"
        };

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request);

        // Assert
        run.InitiatedBy.Should().Be("rehydrated");
    }

    [Fact]
    public void FromDistributionRequest_SetsInitialStep_WhenProvided()
    {
        // Arrange
        var request = CreateMinimalRequest("run-step-test");

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request, initialStep: PipelineStep.GeneratingCode);

        // Assert
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void FromDistributionRequest_DefaultsToCreatedStep_WhenInitialStepIsNull()
    {
        // Arrange
        var request = CreateMinimalRequest("run-default-step");

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request, initialStep: null);

        // Assert
        run.CurrentStep.Should().Be(PipelineStep.Created);
    }

    [Fact]
    public void FromDistributionRequest_SetsAgentIdNull_WhenNotProvided()
    {
        // Arrange
        var request = CreateMinimalRequest("run-no-agent");

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request);

        // Assert
        run.AgentId.Should().BeNull();
    }

    [Fact]
    public void FromDistributionRequest_WithExplicitStartedAt_UsesProvidedTimestamp()
    {
        // Arrange
        var request = CreateMinimalRequest("run-explicit-start");
        var timestamp = new DateTimeOffset(2024, 6, 15, 8, 30, 0, TimeSpan.Zero);

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request, startedAt: timestamp);

        // Assert
        run.StartedAtOffset.Should().Be(timestamp);
#pragma warning disable CS0618
        run.StartedAt.Should().Be(timestamp.UtcDateTime);
#pragma warning restore CS0618
    }

    [Fact]
    public void FromDistributionRequest_WithoutStartedAt_DefaultsToApproximatelyUtcNow()
    {
        // Arrange
        var request = CreateMinimalRequest("run-default-start");
        var before = DateTimeOffset.UtcNow;

        // Act
        var run = PipelineRunFactory.FromDistributionRequest(request);

        // Assert
        var after = DateTimeOffset.UtcNow;
        run.StartedAtOffset.Should().BeOnOrAfter(before);
        run.StartedAtOffset.Should().BeOnOrBefore(after);
    }

    private static JobDistributionRequest CreateMinimalRequest(string runId) => new()
    {
        IssueIdentifier = "owner/repo#1",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "",
        TimeoutSeconds = 1800,
        RunId = runId
    };
}
