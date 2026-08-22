using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="JobAssignmentMessageFactory"/> — the JobDistributionRequest to
/// JobAssignmentMessage mapping served to K8s jobs by WorkItemEndpoints.GetAssignment.
/// </summary>
/// <remarks>
/// These were the BuildJobAssignmentMessage cases of the former DbWorkDistributorBaseTests. The
/// rest of that file exercised DB-backed distributor behaviour that no production class had
/// inherited since KubernetesWorkDistributor became a pure Pipeline API client; it was deleted
/// along with the base class. The dedup predicate those tests covered is now pinned against its
/// authoritative implementation in DispatchDedupEndpointTests
/// (tests/CodingAgentWebUI.Api.IntegrationTests).
///
/// Being a pure mapping, this needs no database — the InMemory fixture the old file carried is gone.
/// </remarks>
public class JobAssignmentMessageFactoryTests
{
    private static JobDistributionRequest CreateRequest(string issueId, string providerId) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = providerId,
        RepoProviderConfigId = "repo-provider-1",
        InitiatedBy = "pipeline-loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,linux",
        TimeoutSeconds = 1800,
        ProjectId = "proj-1",
        RunType = PipelineRunType.Implementation
    };

    [Fact]
    public void BuildJobAssignmentMessage_MapsAllRequiredFields()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#11", "provider-11") with
        {
            AgentProviderConfigId = "agent-config-1",
            BrainProviderConfigId = "brain-1",
            PipelineProviderConfigId = "pipeline-1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#11", Title = "Test", Description = "Desc", Labels = ["bug"] },
            RunType = PipelineRunType.Review,
            ProjectId = "project-x",
            ProjectName = "My Project"
        };

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.JobId.Should().Be(workItemId.ToString());
        message.IssueIdentifier.Should().Be("owner/repo#11");
        message.IssueDetail.Title.Should().Be("Test");
        message.AgentProviderConfigId.Should().Be("agent-config-1");
        message.BrainProviderConfigId.Should().Be("brain-1");
        message.PipelineProviderConfigId.Should().Be("pipeline-1");
        message.RunType.Should().Be(PipelineRunType.Review);
        message.ProjectId.Should().Be("project-x");
        message.ProjectName.Should().Be("My Project");
        message.InitiatedBy.Should().Be("pipeline-loop");
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullOptionals_DefaultsToEmptyCollections()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#12", "provider-12");

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.IssueDetail.Should().NotBeNull();
        message.ParsedIssue.Should().NotBeNull();
        message.IssueComments.Should().BeEmpty();
        message.ProviderConfigs.Should().BeEmpty();
        message.QualityGateConfigs.Should().BeEmpty();
        message.McpServers.Should().BeEmpty();
        message.ReviewerConfigs.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullAgentProviderConfigId_FallsBackToRepoProviderConfigId()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#13", "provider-13") with { AgentProviderConfigId = null };

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.AgentProviderConfigId.Should().Be("repo-provider-1");
    }

    [Fact]
    public void BuildJobAssignmentMessage_MapsConsolidationFields()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("run-123", "consolidation") with
        {
            TaskType = WorkItemTaskType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationTemplateId = "template-42",
            ConsolidationWorkspacePath = "/tmp/consolidation/run-123"
        };

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        message.ConsolidationRunType.Should().Be(ConsolidationRunType.RefactoringDetection);
        message.ConsolidationTemplateId.Should().Be("template-42");
        message.ConsolidationWorkspacePath.Should().Be("/tmp/consolidation/run-123");
    }

    [Fact]
    public void BuildJobAssignmentMessage_NonConsolidation_ConsolidationFieldsAreDefault()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#14", "provider-14");

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.TaskType.Should().Be(WorkItemTaskType.Implementation);
        message.ConsolidationRunType.Should().BeNull();
        message.ConsolidationTemplateId.Should().BeNull();
        message.ConsolidationWorkspacePath.Should().BeNull();
    }
}
