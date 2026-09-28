using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests;

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
        var act = () => resolver.ResolveAsync(job, mockFactory.Object, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

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
        var act = () => resolver.ResolveAsync(job, mockFactory.Object, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        // Assert — NotSupportedException propagates and both earlier providers are disposed
        await act.Should().ThrowAsync<NotSupportedException>();
        mockRepoProvider.Verify(p => p.DisposeAsync(), Times.Once());
        mockAgentProvider.Verify(p => p.DisposeAsync(), Times.Once());
    }

    // TODO: Add a test where additionalRepoProviders have been created (via ProjectContext with
    // DecompositionAnalysis run type) and a subsequent ValidateAsync call fails, verifying that
    // all additional repo providers are also disposed in the catch block.

    // ── ResolveAdditionalRepoProviders — project repositories use their own vended token ──

    [Fact]
    public async Task ResolveAsync_ProjectEpic_CreatesTheOtherProjectReposWithTheProjectRepoFactory()
    {
        // The orchestrator proxy's token refresh only covers the job's primary repository, so the
        // other project repositories must be created by the factory that uses their config's token.
        var resolver = new AgentProviderResolver(_mockLogger.Object);
        var primaryRepoProvider = new Mock<IRepositoryProvider>();
        primaryRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var agentProvider = new Mock<IAgentProvider>();
        agentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var webRepoProvider = new Mock<IRepositoryProvider>();

        var repoConfig = new ProviderConfig
        {
            Id = "repo-api", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "api", Settings = new Dictionary<string, string>()
        };
        var webConfig = new ProviderConfig
        {
            Id = "repo-web", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "web", Settings = new Dictionary<string, string> { ["token"] = "vended-for-web" }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Agent", Settings = new Dictionary<string, string>()
        };

        var jobFactory = new Mock<IProviderFactory>();
        jobFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(primaryRepoProvider.Object);
        jobFactory.Setup(f => f.CreateAgentProvider(agentConfig)).Returns(agentProvider.Object);
        var projectRepoFactory = new Mock<IProviderFactory>();
        projectRepoFactory.Setup(f => f.CreateRepositoryProvider(webConfig)).Returns(webRepoProvider.Object);

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-project-epic",
            IssueIdentifier = "7",
            IssueDetail = new IssueDetail { Identifier = "7", Title = "Epic", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RunType = PipelineRunType.DecompositionAnalysis,
            RepoProviderConfigId = "repo-api",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, webConfig, agentConfig],
            ProjectContext = new DecompositionProjectContext
            {
                ProjectName = "Shop",
                Repositories =
                [
                    new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" },
                    new RepositoryTarget { TemplateName = "web", Description = "", RepoProviderId = "repo-web" }
                ]
            },
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var result = await resolver.ResolveAsync(
            job, jobFactory.Object, projectRepoFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        result.AdditionalRepoProviders.Should().ContainSingle()
            .Which.TemplateName.Should().Be("web");
        projectRepoFactory.Verify(f => f.CreateRepositoryProvider(webConfig), Times.Once);
        jobFactory.Verify(f => f.CreateRepositoryProvider(webConfig), Times.Never);
    }

    // ── ResolveBrainProviderAsync null/skip paths ─────────────────────────

    [Fact]
    public async Task ResolveAsync_EmptyBrainProviderConfigId_ReturnsBrainProviderNull()
    {
        // When the job has no BrainProviderConfigId, brain provider resolution is skipped entirely.
        var resolver = new AgentProviderResolver(_mockLogger.Object);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockAgentProvider.Object);

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "Repo", Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Agent", Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-no-brain",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "",          // explicitly empty — no brain
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var result = await resolver.ResolveAsync(job, mockFactory.Object, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        result.BrainProvider.Should().BeNull();
        // Verify factory was never asked to create a brain provider
        mockFactory.Verify(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == "brain-1")), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_BrainProviderConfigNotInProvidersBag_ReturnsBrainProviderNull()
    {
        // When the job has a BrainProviderConfigId but the config is not in the bag, brain is null.
        var resolver = new AgentProviderResolver(_mockLogger.Object);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockAgentProvider.Object);

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "Repo", Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Agent", Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-brain-missing",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "brain-missing",   // ID present but config not in the bag
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],  // brain config intentionally absent
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var result = await resolver.ResolveAsync(job, mockFactory.Object, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        result.BrainProvider.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_BrainProviderValidationFails_ReturnsBrainProviderNullAndLogs()
    {
        // When ValidateAsync throws on the brain provider, the error is caught,
        // the provider is disposed, and null is returned (brain sync disabled for this run).
        var resolver = new AgentProviderResolver(_mockLogger.Object);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockRepoProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockAgentProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockBrainProvider = new Mock<IRepositoryProvider>();
        mockBrainProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockBrainProvider
            .Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("brain repo not found"));

        var mockFactory = new Mock<IProviderFactory>();
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "Brain", Settings = new Dictionary<string, string>()
        };
        // Distinguish brain from repo provider by ProviderConfig identity
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "Repo", Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Agent", Settings = new Dictionary<string, string>()
        };
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == "repo-1")))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == "brain-1")))
            .Returns(mockBrainProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockAgentProvider.Object);

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-brain-validate-fail",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "brain-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig, brainConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        // Should NOT throw — validation failure for brain is gracefully handled
        var result = await resolver.ResolveAsync(job, mockFactory.Object, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        result.BrainProvider.Should().BeNull();
        // Brain provider was created and then disposed after validation failure
        // TODO [WARNING]: Times.Once() is correct for this scenario (only the ResolveBrainProviderAsync catch
        // path fires). However, if a future refactor merges cleanup paths or adds a second disposal route
        // (e.g., via DisposeAllAsync on the outer catch), DisposeAsync could be called twice, causing this
        // assertion to fail with a misleading "expected 1, was 2" message. Consider AtLeastOnce() if the
        // exact disposal count stops being meaningful, or add a comment when the second disposal path lands.
        mockBrainProvider.Verify(p => p.DisposeAsync(), Times.Once());
        // A warning should have been logged about the validation failure
        // TODO [WARNING]: This Verify targets Warning<T0,T1>(Exception, string, T0, T1) with two string
        // type params — correct only if the production call passes exactly two string-typed template
        // arguments after the message template. If AgentProviderResolver.ResolveBrainProviderAsync
        // is changed to pass a non-string arg or a third arg, the overload resolution changes and this
        // Verify silently matches a different (or no) overload. Verify against the actual production
        // call signature if the log template changes.
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.Is<string>(s => s.Contains("Brain provider validation failed")),
            It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }
}
