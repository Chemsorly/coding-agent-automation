using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for the new parameter-object records introduced in the S107 refactoring:
/// <see cref="DispatchRunRequest"/>, <see cref="ImplementationDispatchOrchestrationRequest"/>,
/// and <see cref="DecompositionDispatchOrchestrationRequest"/>.
/// Verifies property assignment and default values.
/// </summary>
public class DispatchRequestParameterObjectTests
{
    // ── DispatchRunRequest ─────────────────────────────────────────────

    [Fact]
    public void DispatchRunRequest_RequiredProperties_AreAssigned()
    {
        var req = new DispatchRunRequest
        {
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            IssueIdentifier = "owner/repo#42",
            AgentProviderId = "ap-1"
        };

        req.IssueProviderId.Value.Should().Be("ip-1");
        req.RepoProviderId.Value.Should().Be("rp-1");
        req.IssueIdentifier.Should().Be("owner/repo#42");
        req.AgentProviderId.Value.Should().Be("ap-1");
    }

    [Fact]
    public void DispatchRunRequest_DefaultValues_AreCorrect()
    {
        var req = new DispatchRunRequest
        {
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            IssueIdentifier = "x",
            AgentProviderId = "ap"
        };

        req.AgentId.Should().BeNull();
        req.BrainProviderId.Should().BeNull();
        req.PipelineProviderId.Should().BeNull();
        req.InitiatedBy.Should().Be("dispatch");
        req.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void DispatchRunRequest_OptionalProperties_CanBeOverridden()
    {
        var req = new DispatchRunRequest
        {
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            IssueIdentifier = "x",
            AgentProviderId = "ap",
            AgentId = "agent-123",
            BrainProviderId = "bp-1",
            PipelineProviderId = "pp-1",
            InitiatedBy = "loop",
            RunType = PipelineRunType.Review
        };

        req.AgentId.Should().Be("agent-123");
        req.BrainProviderId.Should().Be("bp-1");
        req.PipelineProviderId.Should().Be("pp-1");
        req.InitiatedBy.Should().Be("loop");
        req.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void DispatchRunRequest_With_CreatesNewInstanceWithOverriddenRunType()
    {
        var original = new DispatchRunRequest
        {
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            IssueIdentifier = "x",
            AgentProviderId = "ap",
            RunType = PipelineRunType.Implementation
        };

        var modified = original with { RunType = PipelineRunType.Review };

        modified.RunType.Should().Be(PipelineRunType.Review);
        original.RunType.Should().Be(PipelineRunType.Implementation); // original unchanged
    }

    // ── ImplementationDispatchOrchestrationRequest ─────────────────────

    [Fact]
    public void ImplementationDispatchOrchestrationRequest_RequiredProperties_AreAssigned()
    {
        var project = new PipelineProject { Id = "p-1", Name = "Project" };
        var req = new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier = "owner/repo#10",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            InitiatedBy = "loop",
            Project = project
        };

        req.IssueIdentifier.Should().Be("owner/repo#10");
        req.IssueProviderId.Value.Should().Be("ip-1");
        req.RepoProviderId.Value.Should().Be("rp-1");
        req.InitiatedBy.Should().Be("loop");
        req.Project.Should().BeSameAs(project);
    }

    [Fact]
    public void ImplementationDispatchOrchestrationRequest_DefaultValues_AreCorrect()
    {
        var req = new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier = "x",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "loop",
            Project = new PipelineProject { Id = "", Name = "" }
        };

        req.BrainProviderId.Should().BeNull();
        req.PipelineProviderId.Should().BeNull();
        req.TaskType.Should().Be(WorkItemTaskType.Implementation);
        req.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void ImplementationDispatchOrchestrationRequest_OptionalProperties_CanBeSet()
    {
        var req = new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier = "x",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "manual",
            Project = new PipelineProject { Id = "p", Name = "N" },
            BrainProviderId = "bp-2",
            PipelineProviderId = "pp-2",
            TaskType = WorkItemTaskType.Review,
            RunType = PipelineRunType.Review
        };

        req.BrainProviderId.Should().Be("bp-2");
        req.PipelineProviderId.Should().Be("pp-2");
        req.TaskType.Should().Be(WorkItemTaskType.Review);
        req.RunType.Should().Be(PipelineRunType.Review);
    }

    // ── DecompositionDispatchOrchestrationRequest ──────────────────────

    [Fact]
    public void DecompositionDispatchOrchestrationRequest_RequiredProperties_AreAssigned()
    {
        var project = new PipelineProject { Id = "p-2", Name = "Epic Project" };
        var req = new DecompositionDispatchOrchestrationRequest
        {
            EpicIdentifier = "owner/repo#100",
            EpicTitle = "Big Epic",
            PhaseType = PipelineRunType.DecompositionAnalysis,
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            InitiatedBy = "loop",
            Project = project
        };

        req.EpicIdentifier.Should().Be("owner/repo#100");
        req.EpicTitle.Should().Be("Big Epic");
        req.PhaseType.Should().Be(PipelineRunType.DecompositionAnalysis);
        req.IssueProviderId.Value.Should().Be("ip-1");
        req.RepoProviderId.Value.Should().Be("rp-1");
        req.InitiatedBy.Should().Be("loop");
        req.Project.Should().BeSameAs(project);
    }

    [Fact]
    public void DecompositionDispatchOrchestrationRequest_DefaultValues_AreCorrect()
    {
        var req = new DecompositionDispatchOrchestrationRequest
        {
            EpicIdentifier = "x",
            EpicTitle = "Title",
            PhaseType = PipelineRunType.Decomposition,
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "loop",
            Project = new PipelineProject { Id = "", Name = "" }
        };

        req.BrainProviderId.Should().BeNull();
        req.DecompositionSource.Should().BeNull();
    }

    [Fact]
    public void DecompositionDispatchOrchestrationRequest_DecompositionSource_CanBeSet()
    {
        var req = new DecompositionDispatchOrchestrationRequest
        {
            EpicIdentifier = "x",
            EpicTitle = "Title",
            PhaseType = PipelineRunType.DecompositionAnalysis,
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            InitiatedBy = "loop",
            Project = new PipelineProject { Id = "", Name = "" },
            DecompositionSource = "project-level"
        };

        req.DecompositionSource.Should().Be("project-level");
    }

    // ── Record equality (smoke test) ───────────────────────────────────

    [Fact]
    public void DispatchRunRequest_TwoIdenticalInstances_AreEqual()
    {
        var a = new DispatchRunRequest
        {
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            IssueIdentifier = "x",
            AgentProviderId = "ap"
        };
        var b = new DispatchRunRequest
        {
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            IssueIdentifier = "x",
            AgentProviderId = "ap"
        };

        a.Should().Be(b);
    }
}
