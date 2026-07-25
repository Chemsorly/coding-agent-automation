using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="AgentJobDispatcher.DispatchPipelineContext.Create"/> factory method.
/// Verifies that all 16 properties are correctly mapped from parameters to the returned instance.
/// </summary>
public class DispatchPipelineContextFactoryTests
{
    [Fact]
    public void Create_MapsAllRequiredProperties()
    {
        // Arrange — distinct non-null values for every parameter
        var agent = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" },
            RegisteredAt = DateTimeOffset.UtcNow
        };
        var run = PipelineRun.CreateImplementation(
            runId: "run-123",
            issueIdentifier: "42",
            issueTitle: "Test Issue",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");
        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "Test Profile",
            AgentProviderConfigId = "ap-1",
            MatchLabels = new[] { "dotnet" }
        };
        var issueIdentifier = "issue-42";
        var issueDetail = new IssueDetail
        {
            Identifier = "42",
            Title = "Test Issue Title",
            Description = "Test description",
            Labels = new[] { "bug" }
        };
        var parsedIssue = new ParsedIssue
        {
            AcceptanceCriteria = new[] { "AC1", "AC2" },
            RequirementsSection = "## Requirements\nDo the thing"
        };
        var issueComments = new List<IssueComment>
        {
            new()
            {
                Id = "comment-1",
                Author = "user1",
                Body = "Test comment",
                CreatedAt = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc)
            }
        };
        var repoProviderId = "repo-provider-1";
        var agentProviderId = "agent-provider-1";
        string? brainProviderId = null;
        string? pipelineProviderId = null;
        string? issueProviderId = null;
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Test Repo"
            }
        };
        var config = new PipelineConfiguration();
        var initiatedBy = "test-user";
        var project = new PipelineProject
        {
            Id = "project-1",
            Name = "TestProject"
        };

        // Act
        var ctx = AgentJobDispatcher.DispatchPipelineContext.Create(
            agent, run, profile, issueIdentifier,
            issueDetail, parsedIssue, issueComments,
            repoProviderId, agentProviderId, brainProviderId,
            pipelineProviderId, issueProviderId, providerConfigs,
            config, initiatedBy, project);

        // Assert — all 16 properties
        ctx.Agent.Should().BeSameAs(agent);
        ctx.Run.Should().BeSameAs(run);
        ctx.Profile.Should().BeSameAs(profile);
        ctx.IssueIdentifier.Should().Be(issueIdentifier);
        ctx.IssueDetail.Should().BeSameAs(issueDetail);
        ctx.ParsedIssue.Should().BeSameAs(parsedIssue);
        ctx.IssueComments.Should().BeSameAs(issueComments);
        ctx.RepoProviderId.Should().Be(repoProviderId);
        ctx.AgentProviderId.Should().Be(agentProviderId);
        ctx.BrainProviderId.Should().BeNull();
        ctx.PipelineProviderId.Should().BeNull();
        ctx.IssueProviderId.Should().BeNull();
        ctx.ProviderConfigs.Should().BeSameAs(providerConfigs);
        ctx.Config.Should().BeSameAs(config);
        ctx.InitiatedBy.Should().Be(initiatedBy);
        ctx.Project.Should().BeSameAs(project);
    }

    [Fact]
    public void Create_MapsAllOptionalProperties_WhenNonNull()
    {
        // Arrange — non-null values for all three optional parameters to catch
        // any path that might discard or default them (per brain lesson on factory
        // method extraction regression testing).
        var agent = new AgentEntry
        {
            AgentId = "agent-2",
            ConnectionId = "conn-2",
            Hostname = "host-2",
            Labels = new[] { "python" },
            RegisteredAt = DateTimeOffset.UtcNow
        };
        var run = PipelineRun.CreateImplementation(
            runId: "run-456",
            issueIdentifier: "99",
            issueTitle: "Another Issue",
            issueProviderConfigId: "ip-2",
            repoProviderConfigId: "rp-2");
        var profile = new AgentProfile
        {
            Id = "profile-2",
            DisplayName = "Python Profile",
            AgentProviderConfigId = "ap-2",
            MatchLabels = new[] { "python" }
        };
        var issueDetail = new IssueDetail
        {
            Identifier = "99",
            Title = "Issue with optionals",
            Description = "Has all optional providers set",
            Labels = Array.Empty<string>()
        };
        var parsedIssue = new ParsedIssue
        {
            AcceptanceCriteria = new[] { "AC-opt" },
            RequirementsSection = "Optional test"
        };
        IReadOnlyList<IssueComment> issueComments = Array.Empty<IssueComment>();
        var providerConfigs = new List<ProviderConfig>
        {
            new()
            {
                Id = "rp-2",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Repo 2"
            }
        };
        var config = new PipelineConfiguration();
        var project = new PipelineProject
        {
            Id = "project-2",
            Name = "OptionalProject"
        };

        // Explicit non-null values for the three optional properties
        var brainProviderId = "brain-provider-abc";
        var pipelineProviderId = "pipeline-provider-xyz";
        var issueProviderId = "issue-provider-def";

        // Act
        var ctx = AgentJobDispatcher.DispatchPipelineContext.Create(
            agent, run, profile, "issue-99",
            issueDetail, parsedIssue, issueComments,
            "repo-prov-2", "agent-prov-2", brainProviderId,
            pipelineProviderId, issueProviderId, providerConfigs,
            config, "another-user", project);

        // Assert — all three optional properties are preserved (not null-coalesced or discarded)
        ctx.BrainProviderId.Should().Be("brain-provider-abc");
        ctx.PipelineProviderId.Should().Be("pipeline-provider-xyz");
        ctx.IssueProviderId.Should().Be("issue-provider-def");

        // Also verify remaining required properties for completeness
        ctx.Agent.Should().BeSameAs(agent);
        ctx.Run.Should().BeSameAs(run);
        ctx.Profile.Should().BeSameAs(profile);
        ctx.IssueIdentifier.Should().Be("issue-99");
        ctx.IssueDetail.Should().BeSameAs(issueDetail);
        ctx.ParsedIssue.Should().BeSameAs(parsedIssue);
        ctx.IssueComments.Should().BeSameAs(issueComments);
        ctx.RepoProviderId.Should().Be("repo-prov-2");
        ctx.AgentProviderId.Should().Be("agent-prov-2");
        ctx.ProviderConfigs.Should().BeSameAs(providerConfigs);
        ctx.Config.Should().BeSameAs(config);
        ctx.InitiatedBy.Should().Be("another-user");
        ctx.Project.Should().BeSameAs(project);
    }
}
