using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Constructor / property coverage tests for the request-object records introduced
/// to satisfy S107 (too many parameters).  These tests exercise the generated primary
/// constructors so that Sonar counts the lines as covered.
/// </summary>
public class DispatchRequestRecordTests
{
    // ── DispatchRunRequest ──────────────────────────────────────────────────

    [Fact]
    public void DispatchRunRequest_RequiredPropertiesAreSet()
    {
        var req = new DispatchRunRequest
        {
            IssueProviderId    = new ProviderConfigId("issue-cfg"),
            RepoProviderId     = new ProviderConfigId("repo-cfg"),
            IssueIdentifier    = "owner/repo#99",
            AgentProviderId    = new ProviderConfigId("agent-cfg"),
        };

        req.IssueProviderId.Value.Should().Be("issue-cfg");
        req.RepoProviderId.Value.Should().Be("repo-cfg");
        req.IssueIdentifier.Value.Should().Be("owner/repo#99");
        req.AgentProviderId.Value.Should().Be("agent-cfg");
        req.AgentId.Should().BeNull();
        req.BrainProviderId.Should().BeNull();
        req.PipelineProviderId.Should().BeNull();
        req.InitiatedBy.Should().Be("dispatch");           // default value
        req.RunType.Should().Be(PipelineRunType.Implementation); // default value
    }

    [Fact]
    public void DispatchRunRequest_OptionalPropertiesCanBeSet()
    {
        var req = new DispatchRunRequest
        {
            IssueProviderId   = new ProviderConfigId("issue-cfg"),
            RepoProviderId    = new ProviderConfigId("repo-cfg"),
            IssueIdentifier   = "owner/repo#1",
            AgentProviderId   = new ProviderConfigId("agent-cfg"),
            AgentId           = "agent-instance-42",
            BrainProviderId   = "brain-cfg",
            PipelineProviderId = "pipeline-cfg",
            InitiatedBy       = "loop",
            RunType           = PipelineRunType.Review,
        };

        req.AgentId.Should().Be("agent-instance-42");
        req.BrainProviderId.Should().Be("brain-cfg");
        req.PipelineProviderId.Should().Be("pipeline-cfg");
        req.InitiatedBy.Should().Be("loop");
        req.RunType.Should().Be(PipelineRunType.Review);
    }

    // ── ImplementationDispatchOrchestrationRequest ──────────────────────────

    [Fact]
    public void ImplementationDispatchOrchestrationRequest_RequiredPropertiesAreSet()
    {
        var project = new PipelineProject { Id = "proj-1", Name = "MyProject" };

        var req = new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier  = "owner/repo#5",
            IssueProviderId  = "issue-cfg",
            RepoProviderId   = "repo-cfg",
            InitiatedBy      = "user",
            Project          = project,
        };

        req.IssueIdentifier.Value.Should().Be("owner/repo#5");
        req.IssueProviderId.Value.Should().Be("issue-cfg");
        req.RepoProviderId.Value.Should().Be("repo-cfg");
        req.InitiatedBy.Should().Be("user");
        req.Project.Should().BeSameAs(project);
        req.BrainProviderId.Should().BeNull();
        req.PipelineProviderId.Should().BeNull();
        req.TaskType.Should().Be(WorkItemTaskType.Implementation);
        req.RunType.Should().Be(PipelineRunType.Implementation);
    }

    // ── DecompositionDispatchOrchestrationRequest ───────────────────────────

    [Fact]
    public void DecompositionDispatchOrchestrationRequest_RequiredPropertiesAreSet()
    {
        var project = new PipelineProject { Id = "proj-2", Name = "Decomp" };

        var req = new DecompositionDispatchOrchestrationRequest
        {
            EpicIdentifier   = "owner/repo#100",
            EpicTitle        = "Build the thing",
            PhaseType        = PipelineRunType.Decomposition,
            IssueProviderId  = "issue-cfg",
            RepoProviderId   = "repo-cfg",
            InitiatedBy      = "user",
            Project          = project,
        };

        req.EpicIdentifier.Value.Should().Be("owner/repo#100");
        req.EpicTitle.Should().Be("Build the thing");
        req.PhaseType.Should().Be(PipelineRunType.Decomposition);
        req.BrainProviderId.Should().BeNull();
        req.DecompositionSource.Should().BeNull();
    }
}
