using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="ActiveJobStateFactory"/>.
/// Verifies: ModelName resolution from ProviderConfigs, RepositoryName derivation,
/// and correct mapping of all fields from <see cref="JobAssignmentMessage"/>.
/// </summary>
public class ActiveJobStateFactoryTests
{
    // ── ModelName Resolution ─────────────────────────────────────────────

    [Fact]
    public void Create_WithModelInAgentProviderConfig_ResolvesModelName()
    {
        var assignment = CreateAssignment(agentProviderConfigId: "agent-1", providerConfigs:
        [
            new ProviderConfig
            {
                Id = "agent-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Test Agent",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet-4.6" }
            }
        ]);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.ModelName.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public void Create_WithNoProviderConfigs_ModelNameIsNull()
    {
        var assignment = CreateAssignment(agentProviderConfigId: "agent-1", providerConfigs: []);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.ModelName.Should().BeNull();
    }

    [Fact]
    public void Create_WithAgentConfigMissingModelSetting_ModelNameIsNull()
    {
        var assignment = CreateAssignment(agentProviderConfigId: "agent-1", providerConfigs:
        [
            new ProviderConfig
            {
                Id = "agent-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "No Model",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro" }
            }
        ]);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.ModelName.Should().BeNull();
    }

    [Fact]
    public void Create_WithModelSetToAuto_ResolvesAuto()
    {
        var assignment = CreateAssignment(agentProviderConfigId: "agent-1", providerConfigs:
        [
            new ProviderConfig
            {
                Id = "agent-1",
                Kind = ProviderKind.Agent,
                ProviderType = "KiroCli",
                DisplayName = "Auto Agent",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "auto" }
            }
        ]);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.ModelName.Should().Be("auto");
    }

    // ── RepositoryName Resolution ────────────────────────────────────────

    [Fact]
    public void Create_WithRepoProviderConfig_ResolvesRepositoryName()
    {
        var assignment = CreateAssignment(repoProviderConfigId: "repo-1", providerConfigs:
        [
            new ProviderConfig
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Test Repo",
                Settings = new Dictionary<string, string>
                {
                    [ProviderSettingKeys.Owner] = "Chemsorly",
                    [ProviderSettingKeys.Repo] = "coding-agent-automation"
                }
            }
        ]);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.RepositoryName.Should().Be("Chemsorly/coding-agent-automation");
    }

    [Fact]
    public void Create_WithNoRepoProviderConfig_RepositoryNameIsNull()
    {
        var assignment = CreateAssignment(repoProviderConfigId: "repo-missing", providerConfigs: []);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.RepositoryName.Should().BeNull();
    }

    [Fact]
    public void Create_WithRepoConfigMissingOwnerOrRepo_RepositoryNameIsNull()
    {
        var assignment = CreateAssignment(repoProviderConfigId: "repo-1", providerConfigs:
        [
            new ProviderConfig
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "Partial",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Owner] = "Chemsorly" }
                // Missing Repo
            }
        ]);

        var state = ActiveJobStateFactory.Create("run-123", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.RepositoryName.Should().BeNull();
    }

    // ── Core Field Mapping ───────────────────────────────────────────────

    [Fact]
    public void Create_MapsAllCoreFields()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var assignment = CreateAssignment(
            issueIdentifier: "owner/repo#42",
            issueTitle: "Fix the thing",
            issueProviderConfigId: "issue-provider-1",
            repoProviderConfigId: "repo-1",
            agentProviderConfigId: "agent-1",
            brainProviderConfigId: "brain-1",
            pipelineProviderConfigId: "pipeline-1",
            initiatedBy: "closed-loop",
            resolvedProfileId: "profile-x",
            projectId: "proj-99",
            projectName: "My Project",
            runType: PipelineRunType.Review);

        var state = ActiveJobStateFactory.Create("run-abc", assignment, PipelineStep.RunningQualityGates, startedAt);

        state.RunId.Should().Be("run-abc");
        state.IssueIdentifier.Should().Be("owner/repo#42");
        state.IssueTitle.Should().Be("Fix the thing");
        state.IssueProviderConfigId.Should().Be("issue-provider-1");
        state.RepoProviderConfigId.Should().Be("repo-1");
        state.AgentProviderConfigId.Should().Be("agent-1");
        state.BrainProviderConfigId.Should().Be("brain-1");
        state.PipelineProviderConfigId.Should().Be("pipeline-1");
        state.InitiatedBy.Should().Be("closed-loop");
        state.ResolvedProfileId.Should().Be("profile-x");
        state.ProjectId.Should().Be("proj-99");
        state.ProjectName.Should().Be("My Project");
        state.CurrentStep.Should().Be(PipelineStep.RunningQualityGates);
        state.StartedAt.Should().Be(startedAt);
        state.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void Create_NullIssueProviderConfigId_FallsBackToRepoProviderConfigId()
    {
        var assignment = CreateAssignment(
            issueProviderConfigId: null,
            repoProviderConfigId: "repo-fallback");

        var state = ActiveJobStateFactory.Create("run-x", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.IssueProviderConfigId.Should().Be("repo-fallback");
    }

    [Fact]
    public void Create_NullIssueTitle_FallsBackToIssueIdentifier()
    {
        var assignment = CreateAssignment(issueIdentifier: "owner/repo#7", issueTitle: null);

        var state = ActiveJobStateFactory.Create("run-x", assignment, PipelineStep.Created, DateTimeOffset.UtcNow);

        state.IssueTitle.Should().Be("owner/repo#7");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static JobAssignmentMessage CreateAssignment(
        string issueIdentifier = "owner/repo#1",
        string? issueTitle = "Test Issue",
        string? issueProviderConfigId = "issue-1",
        string repoProviderConfigId = "repo-1",
        string agentProviderConfigId = "agent-1",
        string? brainProviderConfigId = null,
        string? pipelineProviderConfigId = null,
        string initiatedBy = "test",
        string? resolvedProfileId = null,
        string? projectId = null,
        string? projectName = null,
        PipelineRunType runType = PipelineRunType.Implementation,
        IReadOnlyList<ProviderConfig>? providerConfigs = null)
    {
        return new JobAssignmentMessage
        {
            JobId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueIdentifier,
            IssueDetail = new IssueDetail
            {
                Identifier = issueIdentifier,
                Title = issueTitle ?? "",
                Description = "",
                Labels = []
            },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [],
            RepoProviderConfigId = repoProviderConfigId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            PipelineProviderConfigId = pipelineProviderConfigId,
            ProviderConfigs = providerConfigs ?? [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = initiatedBy,
            ResolvedProfileId = resolvedProfileId,
            QualityGateConfigs = [],
            RunType = runType,
            ProjectId = projectId,
            ProjectName = projectName,
            IssueProviderConfigId = issueProviderConfigId
        };
    }
}
