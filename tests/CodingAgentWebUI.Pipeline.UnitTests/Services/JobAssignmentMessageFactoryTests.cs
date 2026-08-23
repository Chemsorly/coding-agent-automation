using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
}
