using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="JobDeduplicationGuardService"/>.
/// Queue methods (EnqueueJob, DequeueForAgent, MarkIssueComplete, etc.) were deleted in T18
/// (arch-audit 2026-08-22) as they were provably no-ops; those test cases have been removed.
/// </summary>
public class JobDeduplicationGuardServiceTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(new Mock<ILogger>().Object);

    private static JobDeduplicationGuardService CreateService(AgentRegistryService? registry = null) =>
        new(registry ?? CreateRegistry(), new Mock<ILogger>().Object);

    #region SelectAgent

    [Fact]
    public void SelectAgent_NoIdleAgents_ReturnsNull()
    {
        var registry = CreateRegistry();
        var service = CreateService(registry);

        service.SelectAgent(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void SelectAgent_EmptyLabels_MatchesAnyAgent()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(Array.Empty<string>());
        agent.Should().NotBeNull();
        agent!.AgentId.Value.Should().Be("agent-1");
    }

    [Fact]
    public void SelectAgent_MatchingLabels_ReturnsAgent()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = new[] { "dotnet", "linux" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(new[] { "dotnet" });
        agent.Should().NotBeNull();
    }

    [Fact]
    public void SelectAgent_NonMatchingLabels_ReturnsNull()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = new[] { "python" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(new[] { "dotnet" });
        agent.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_NullLabels_Throws()
    {
        var service = CreateService();
        var act = () => service.SelectAgent(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ResolveRequiredLabels

    [Fact]
    public void ResolveRequiredLabels_RepoConfigHasLabels_ReturnsRepoLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string> { "dotnet", "linux" }
        };
        var pipelineConfig = new PipelineConfiguration();

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "dotnet", "linux" });
    }

    [Fact]
    public void ResolveRequiredLabels_NoRepoLabels_FallsToPipelineDefault()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "kiro, agent"
        };

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "kiro", "agent" });
    }

    [Fact]
    public void ResolveRequiredLabels_NoLabelsAnywhere_ReturnsEmpty()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new PipelineConfiguration();

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEmpty();
    }

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_FallsToPipelineDefault()
    {
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "default-label"
        };

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(null, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "default-label" });
    }

    #endregion
}
