using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentProviderResolver"/>.
/// Verifies that partial provider creation failures trigger disposal of already-created providers.
/// </summary>
public class AgentProviderResolverTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    // ── ResolveAsync — Provider Creation Failure Disposes Earlier Providers ──
    // TODO: Add a test where one DisposeAsync call throws during cleanup and verify remaining providers
    // are still disposed (resilient cleanup via ProviderDisposer).

    [Fact]
    public async Task ResolveAsync_AgentProviderCreationFails_DisposesRepoProviderAndThrows()
    {
        // Arrange — mock factory where CreateRepositoryProvider succeeds but CreateAgentProvider throws.
        var resolver = new AgentProviderResolver(_mockLogger.Object);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Throws(new NotSupportedException("Unsupported agent type"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "UnsupportedAgentType",
            DisplayName = "Bad Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-agent-creation-fail",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        // Act — CreateAgentProvider throws after repoProvider is already created
        var act = () => resolver.ResolveAsync(job, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        // Assert — NotSupportedException propagates and repoProvider.DisposeAsync was called
        await act.Should().ThrowAsync<NotSupportedException>();
        mockRepoProvider.Verify(p => p.DisposeAsync(), Times.Once());
    }

    [Fact]
    public async Task ResolveAsync_PipelineProviderCreationFails_DisposesEarlierProvidersAndThrows()
    {
        // Arrange — mock factory where repo+agent creation succeeds but pipeline provider creation throws.
        var resolver = new AgentProviderResolver(_mockLogger.Object);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockAgentProvider.Object);
        mockFactory.Setup(f => f.CreatePipelineProviderAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("Unsupported pipeline type"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new ProviderConfig
        {
            Id = "pipeline-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "UnsupportedPipelineType",
            DisplayName = "Bad Pipeline",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-pipeline-creation-fail",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineProviderConfigId = "pipeline-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig, pipelineConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        // Act — CreatePipelineProviderAsync throws after repo+agent providers are created
        var act = () => resolver.ResolveAsync(job, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        // Assert — NotSupportedException propagates and both earlier providers are disposed
        await act.Should().ThrowAsync<NotSupportedException>();
        mockRepoProvider.Verify(p => p.DisposeAsync(), Times.Once());
        mockAgentProvider.Verify(p => p.DisposeAsync(), Times.Once());
    }

    // TODO: Add a test where additionalRepoProviders have been created (via ProjectContext with
    // DecompositionAnalysis run type) and a subsequent ValidateAsync call fails, verifying that
    // all additional repo providers are also disposed in the catch block.
}
