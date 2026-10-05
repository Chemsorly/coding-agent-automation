using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Tests for JobAssignmentMessageFactory.BuildJobAssignmentMessage.
/// Covers: field mapping, null-safe defaults for optional fields, IssueIdentifier passthrough.
/// </summary>
public sealed class JobAssignmentMessageFactoryTests
{
    private static JobDistributionRequest MinimalRequest() => new()
    {
        IssueIdentifier = new IssueIdentifier("GH-42"),
        IssueProviderConfigId = "github",
        RepoProviderConfigId = "github-repo",
        InitiatedBy = "user",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro",
        TimeoutSeconds = 3600
    };

    // ── Core field mapping ────────────────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_SetsJobId()
    {
        var id = Guid.NewGuid();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(id, MinimalRequest());
        msg.JobId.Should().Be(id.ToString());
    }

    [Fact]
    public void BuildJobAssignmentMessage_SetsIssueIdentifier()
    {
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), MinimalRequest());
        msg.IssueIdentifier.Should().Be(new IssueIdentifier("GH-42"));
    }

    [Fact]
    public void BuildJobAssignmentMessage_SetsInitiatedBy()
    {
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), MinimalRequest());
        msg.InitiatedBy.Should().Be("user");
    }

    [Fact]
    public void BuildJobAssignmentMessage_SetsRepoProviderConfigId()
    {
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), MinimalRequest());
        msg.RepoProviderConfigId.Should().Be("github-repo");
    }

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
            ProjectId = new Guid("22220000-0000-0000-0000-000000000001"),
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
        message.ProjectId.Should().Be(new Guid("22220000-0000-0000-0000-000000000001").ToString());
        message.ProjectName.Should().Be("My Project");
        message.InitiatedBy.Should().Be("pipeline-loop");
    }

    // ── Null defaults ─────────────────────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_NullIssueDetail_BuildsDefaultIssueDetail()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);

        msg.IssueDetail.Should().NotBeNull();
        msg.IssueDetail.Title.Should().BeEmpty();
        msg.IssueDetail.Labels.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullParsedIssue_BuildsDefaultParsedIssue()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);

        msg.ParsedIssue.Should().NotBeNull();
        msg.ParsedIssue.AcceptanceCriteria.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullIssueComments_EmptyList()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.IssueComments.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullProviderConfigs_EmptyList()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.ProviderConfigs.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullPipelineConfig_BuildsDefault()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.PipelineConfiguration.Should().NotBeNull();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullQualityGateConfigs_EmptyList()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.QualityGateConfigs.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullMcpServers_EmptyList()
    {
        var req = MinimalRequest();
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullAgentProviderConfigId_FallsBackToRepoProvider()
    {
        var req = MinimalRequest();
        // AgentProviderConfigId not set — should fall back to RepoProviderConfigId
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.AgentProviderConfigId.Should().Be("github-repo");
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

    // ── Provided values are used ──────────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_WithIssueDetail_UsesProvided()
    {
        var req = MinimalRequest() with
        {
            IssueDetail = new IssueDetail
            {
                Identifier = new IssueIdentifier("GH-42"),
                Title = "Fix bug",
                Description = "desc",
                Labels = ["bug"]
            }
        };
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.IssueDetail.Title.Should().Be("Fix bug");
    }

    [Fact]
    public void BuildJobAssignmentMessage_WithAgentProviderConfigId_UsesProvided()
    {
        var req = MinimalRequest() with { AgentProviderConfigId = "kiro-agent" };
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.AgentProviderConfigId.Should().Be("kiro-agent");
    }

    [Fact]
    public void BuildJobAssignmentMessage_RunType_IsPassedThrough()
    {
        var req = MinimalRequest() with { RunType = PipelineRunType.Review };
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void BuildJobAssignmentMessage_TaskType_IsPassedThrough()
    {
        var req = MinimalRequest() with { TaskType = WorkItemTaskType.Decomposition };
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.TaskType.Should().Be(WorkItemTaskType.Decomposition);
    }

    [Fact]
    public void BuildJobAssignmentMessage_ForceRefreshAnalysis_IsPassedThrough()
    {
        var req = MinimalRequest() with { ForceRefreshAnalysis = true };
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);
        msg.ForceRefreshAnalysis.Should().BeTrue();
    }

    // ── Project review ────────────────────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_ProjectReview_PassesTheReviewersAndRepositoriesOn()
    {
        var req = MinimalRequest() with
        {
            ProjectReviewers = [new ReviewAgent { Name = "ProjectReviewer", Prompt = "Check the project." }],
            ProjectReviewRepositories = [new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" }]
        };

        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);

        msg.ProjectReviewers.Select(r => r.Name).Should().Equal("ProjectReviewer");
        msg.ProjectReviewRepositories!.Select(r => r.RepoProviderId).Should().Equal("repo-api");
    }

    [Fact]
    public void BuildJobAssignmentMessage_NoProjectReview_HasNoReviewersOrRepositories()
    {
        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), MinimalRequest());

        msg.ProjectReviewers.Should().BeEmpty();
        msg.ProjectReviewRepositories.Should().BeNull();
    }

    [Fact]
    public void BuildJobAssignmentMessage_CopiesConsolidationHistoryContext()
    {
        var lastSuccess = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var req = MinimalRequest() with
        {
            ConsolidationLastSuccessfulRunUtc = lastSuccess,
            ConsolidationFeedbackDataJson = "[{\"outcome\":\"Failure\"}]"
        };

        var msg = JobAssignmentMessageFactory.BuildJobAssignmentMessage(Guid.NewGuid(), req);

        msg.ConsolidationLastSuccessfulRunUtc.Should().Be(lastSuccess);
        msg.ConsolidationFeedbackDataJson.Should().Be("[{\"outcome\":\"Failure\"}]");
    }

    // ── Consolidation fields ────────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_MapsConsolidationFields()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("run-123", "consolidation") with
        {
            TaskType = WorkItemTaskType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationTemplateId = "template-42"
        };

        var message = JobAssignmentMessageFactory.BuildJobAssignmentMessage(workItemId, request);

        message.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        message.ConsolidationRunType.Should().Be(ConsolidationRunType.RefactoringDetection);
        message.ConsolidationTemplateId.Should().Be("template-42");
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
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest(string issueId, string providerId) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = providerId,
        RepoProviderConfigId = "repo-provider-1",
        InitiatedBy = "pipeline-loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,linux",
        TimeoutSeconds = 1800,
        ProjectId = new Guid("11110000-0000-0000-0000-000000000001"),
        RunType = PipelineRunType.Implementation
    };
}
