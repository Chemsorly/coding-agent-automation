using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for the <c>providerFactoryOverride</c> test seam of <see cref="ConsolidationProviderResolver"/>:
/// when it is set, every provider, the issue provider included, comes from the override instead of the real
/// <see cref="AgentProviderFactory"/> and the static GitHub/GitLab issue provider construction.
/// </summary>
public class ConsolidationProviderResolverOverrideTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    [Fact]
    public async Task ResolveRefactoringProviders_WithOverride_TakesRepoAgentAndIssueFromIt()
    {
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var issueProvider = new Mock<IIssueProvider>();
        repoProvider.Setup(r => r.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        agentProvider.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        issueProvider.Setup(i => i.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(repoProvider.Object);
        factory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(agentProvider.Object);
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(issueProvider.Object);

        // The issue config has no owner, repo or token: without the override the static GitHub construction throws.
        var job = CreateJob(ConsolidationRunType.RefactoringDetection,
        [
            CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work),
            CreateProviderConfig(ProviderKind.Agent, "KiroCli"),
            CreateProviderConfig(ProviderKind.Issue, "GitHub")
        ]);

        var result = await CreateResolver(factory.Object).ResolveRefactoringProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Providers!.RepoProvider.Should().BeSameAs(repoProvider.Object);
        result.Providers.AgentProvider.Should().BeSameAs(agentProvider.Object);
        result.Providers.IssueProvider.Should().BeSameAs(issueProvider.Object);
    }

    [Fact]
    public async Task ResolveBrainConsolidationProviders_WithOverride_TakesBrainAndAgentFromIt()
    {
        var brainProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        brainProvider.Setup(r => r.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        agentProvider.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(brainProvider.Object);
        factory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(agentProvider.Object);

        var job = CreateJob(ConsolidationRunType.BrainConsolidation,
        [
            CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Brain),
            CreateProviderConfig(ProviderKind.Agent, "KiroCli")
        ]);

        var result = await CreateResolver(factory.Object).ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Providers!.BrainProvider.Should().BeSameAs(brainProvider.Object);
        result.Providers.AgentProvider.Should().BeSameAs(agentProvider.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ConsolidationProviderResolver CreateResolver(IProviderFactory providerFactoryOverride) =>
        new(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object,
            providerFactoryOverride: providerFactoryOverride);

    private static ConsolidationJobMessage CreateJob(
        ConsolidationRunType type,
        IReadOnlyList<ProviderConfig> providerConfigs) => new()
        {
            JobId = $"job-{Guid.NewGuid():N}",
            Type = type,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = new PipelineConfiguration()
        };

    private static ProviderConfig CreateProviderConfig(
        ProviderKind kind,
        string providerType,
        RepositoryRole? role = null) => new()
        {
            Id = $"{kind}-{Guid.NewGuid():N}",
            Kind = kind,
            ProviderType = providerType,
            DisplayName = $"Test {kind}",
            RepositoryRole = role ?? RepositoryRole.Work,
            Settings = new Dictionary<string, string>()
        };
}
