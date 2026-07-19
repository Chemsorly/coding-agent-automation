using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Characterization tests that capture the exact <see cref="JobAssignmentMessage"/> produced by
/// each dispatch path. These tests assert on ALL message properties to detect property-mapping
/// regressions during refactoring.
/// </summary>
public class AgentJobDispatcherCharacterizationTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<ILabelService> _mockLabelService;
    private readonly Mock<IAgentCommunication> _mockAgentComm;
    private readonly HttpClient _httpClient;
    private readonly TokenVendingService _tokenVending;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly List<PipelineOrchestrationService> _orchestrationInstances = new();

    private JobAssignmentMessage? _capturedMessage;

    public AgentJobDispatcherCharacterizationTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockLabelService = new Mock<ILabelService>();
        _mockAgentComm = new Mock<IAgentCommunication>();
        _httpClient = new HttpClient();
        _tokenVending = new TokenVendingService(_mockLogger.Object, _httpClient);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();

        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });
        _mockConfigStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        // Capture the JobAssignmentMessage sent to the agent
        _mockAgentComm
            .Setup(c => c.AssignJobAsync(It.IsAny<string>(), It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, JobAssignmentMessage, CancellationToken>((_, msg, _) => _capturedMessage = msg)
            .Returns(Task.CompletedTask);
    }

    private AgentJobDispatcher CreateDispatcher()
    {
        var realOrchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            executionFacade: new PipelineExecutionFacade(
                new AgentPhaseExecutor(_mockLogger.Object),
                new QualityGateExecutor(new Mock<IQualityGateValidator>().Object, new PullRequestOrchestrator(_mockLogger.Object), new CiLogWriter(_mockLogger.Object), new FeedbackService(_mockLogger.Object), _mockLogger.Object),
                new Mock<IQualityGateValidator>().Object,
                Mock.Of<IBrainSyncService>()),
            historyService: _mockHistoryService.Object,
            runService: _runService);
        _orchestrationInstances.Add(realOrchestration);

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            historyService: _mockHistoryService.Object,
            runService: _runService);

        return new AgentJobDispatcher(
            _dispatcher,
            _registry,
            _runService,
            runCreator,
            new DispatchInfrastructure(
                _tokenVending,
                _mockProviderFactory.Object,
                _mockLabelService.Object,
                new DispatchResolutionService(
                    new ProfileResolver(),
                    new QualityGateResolver(),
                    new ReviewerResolver(),
                    _mockConfigStore.Object,
                    _mockLogger.Object)),
            _mockAgentComm.Object,
            new ShutdownSignal(),
            _mockLogger.Object);
    }

    private void SetupHappyPathMocks(string agentProviderId, string? steeringContent = null)
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "profile-1",
                    DisplayName = "Test Profile",
                    AgentProviderConfigId = agentProviderId,
                    MatchLabels = Array.Empty<string>(),
                    McpServers = new List<McpServerConfig>
                    {
                        new() { Name = "test-mcp", Command = "test-cmd" }
                    }
                }
            });
        _mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        var repoConfig = new ProviderConfig
        {
            Id = "rp",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            SteeringContent = steeringContent
        };
        var agentConfig = new ProviderConfig
        {
            Id = agentProviderId,
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string> { ["model"] = "auto" }
        };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { repoConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { agentConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private void SetupIssueProviderMock(IReadOnlyList<IssueComment> comments, string issueTitle = "Test Issue", string issueDescription = "Test description")
    {
        var issueConfig = new ProviderConfig
        {
            Id = "ip",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "issue-1",
                Title = issueTitle,
                Description = issueDescription,
                Labels = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    #region Implementation Dispatch Characterization

    [Fact]
    public async Task DispatchToAgentAsync_Characterization_AllPropertiesCorrectlyMapped()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-char",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-char");

        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "TestProject",
            Secrets = new Dictionary<string, string> { ["SECRET_KEY"] = "secret-value" },
            SteeringContent = "project steering content"
        };

        SetupHappyPathMocks("agent-provider-1", steeringContent: "repo steering content");
        SetupIssueProviderMock(new List<IssueComment>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchToAgentAsync(
            agent, "issue-char", "ip", "rp", null, null, "user",
            Array.Empty<string>(), CancellationToken.None, project);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();
        var msg = _capturedMessage!;

        // Shared properties
        msg.JobId.Should().NotBeNullOrEmpty();
        msg.IssueIdentifier.Should().Be("issue-char");
        msg.IssueDetail.Should().NotBeNull();
        msg.IssueDetail.Title.Should().Be("Test Issue");
        msg.ParsedIssue.Should().NotBeNull();
        msg.IssueComments.Should().BeEmpty();
        msg.RepoProviderConfigId.Should().Be("rp");
        msg.AgentProviderConfigId.Should().Be("agent-provider-1");
        msg.BrainProviderConfigId.Should().BeNull();
        msg.PipelineProviderConfigId.Should().BeNull();
        msg.ProviderConfigs.Should().NotBeNull();
        msg.PipelineConfiguration.Should().NotBeNull();
        msg.InitiatedBy.Should().Be("user");
        msg.ResolvedProfileId.Should().Be("profile-1");
        msg.McpServers.Should().HaveCount(1);
        msg.McpServers[0].Name.Should().Be("test-mcp");
        msg.ProjectId.Should().Be("proj-1");
        msg.ProjectName.Should().Be("TestProject");
        msg.ProjectSecrets.Should().ContainKey("SECRET_KEY");
        msg.ProjectSteeringContent.Should().Be("project steering content");
        msg.RepoSteeringContent.Should().BeNull(); // TokenVendingService.CloneWithSettings does not copy SteeringContent
        msg.TraceContext.Should().BeNull(); // No active trace in test
        msg.IssueProviderConfigId.Should().Be("ip");

        // Implementation-specific properties
        msg.ExistingAnalysis.Should().BeNull();
        msg.ForceRefreshAnalysis.Should().BeFalse();
        msg.StalenessSignal.Should().BeNull();
        msg.AnalysisRefreshCount.Should().Be(0);
        msg.QualityGateConfigs.Should().BeEmpty(); // No QGCs configured in test
        msg.ReviewerConfigs.Should().BeEmpty(); // No reviewer configs in test

        // Properties that should NOT be set for implementation
        msg.RunType.Should().Be(PipelineRunType.Implementation);
        msg.LinkedPullRequest.Should().BeNull();
        msg.LinkedIssueContexts.Should().BeNull();
        msg.ProjectContext.Should().BeNull();
        msg.ReviewPrTargetBranch.Should().BeNull();
        msg.ReviewPrDescription.Should().BeNull();
        msg.ReviewPrAuthor.Should().BeNull();
    }

    #endregion

    #region Review Dispatch Characterization

    [Fact]
    public async Task DispatchReviewToAgentAsync_Characterization_AllPropertiesCorrectlyMapped()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-review-char",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-review-char");

        var project = new PipelineProject
        {
            Id = "proj-2",
            Name = "ReviewProject",
            Secrets = new Dictionary<string, string> { ["REVIEW_SECRET"] = "review-value" },
            SteeringContent = "review project steering"
        };

        SetupHappyPathMocks("agent-provider-1", steeringContent: "repo steering for review");

        // Setup repo provider mock for ExtractLinkedIssuesAsync
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.RepositoryFullName).Returns("org/repo");
        mockRepoProvider.Setup(r => r.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchReviewToAgentAsync(
            agent,
            new ReviewDispatchRequest
            {
                PrIdentifier = "42",
                PrBranchName = "feature/test",
                PrTitle = "Test PR Title",
                PrUrl = "https://github.com/org/repo/pull/42",
                PrTargetBranch = "main",
                PrDescription = "PR description body",
                PrAuthor = "test-author",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                BrainProviderId = null,
                InitiatedBy = "reviewer"
            },
            Array.Empty<string>(),
            CancellationToken.None,
            project);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();
        var msg = _capturedMessage!;

        // Shared properties
        msg.JobId.Should().NotBeNullOrEmpty();
        msg.IssueIdentifier.Should().Be("42");
        msg.IssueDetail.Should().NotBeNull();
        msg.IssueDetail.Title.Should().Be("Test PR Title");
        msg.IssueDetail.Description.Should().Be("PR description body");
        msg.ParsedIssue.Should().NotBeNull();
        msg.IssueComments.Should().BeEmpty();
        msg.RepoProviderConfigId.Should().Be("rp");
        msg.AgentProviderConfigId.Should().Be("agent-provider-1");
        msg.BrainProviderConfigId.Should().BeNull();
        msg.PipelineProviderConfigId.Should().BeNull();
        msg.ProviderConfigs.Should().NotBeNull();
        msg.PipelineConfiguration.Should().NotBeNull();
        msg.InitiatedBy.Should().Be("reviewer");
        msg.ResolvedProfileId.Should().Be("profile-1");
        msg.McpServers.Should().HaveCount(1);
        msg.ProjectId.Should().Be("proj-2");
        msg.ProjectName.Should().Be("ReviewProject");
        msg.ProjectSecrets.Should().ContainKey("REVIEW_SECRET");
        msg.ProjectSteeringContent.Should().Be("review project steering");
        msg.RepoSteeringContent.Should().BeNull(); // TokenVendingService.CloneWithSettings does not copy SteeringContent
        msg.IssueProviderConfigId.Should().Be("ip");

        // Review-specific properties
        msg.RunType.Should().Be(PipelineRunType.Review);
        msg.LinkedPullRequest.Should().NotBeNull();
        msg.LinkedPullRequest!.Number.Should().Be(42);
        msg.LinkedPullRequest.BranchName.Should().Be("feature/test");
        msg.LinkedPullRequest.Url.Should().Be("https://github.com/org/repo/pull/42");
        msg.ReviewPrTargetBranch.Should().Be("main");
        msg.ReviewPrDescription.Should().Be("PR description body");
        msg.ReviewPrAuthor.Should().Be("test-author");
        msg.QualityGateConfigs.Should().BeEmpty();
        msg.ReviewerConfigs.Should().BeEmpty(); // No reviewer configs configured in test
        msg.LinkedIssueContexts.Should().BeNull(); // No linked issues found

        // Properties that should NOT be set for review
        msg.ExistingAnalysis.Should().BeNull();
        msg.ForceRefreshAnalysis.Should().BeFalse();
        msg.ProjectContext.Should().BeNull();
    }

    #endregion

    #region Decomposition Dispatch Characterization

    [Fact]
    public async Task DispatchDecompositionToAgentAsync_Phase1_Characterization_AllPropertiesCorrectlyMapped()
    {
        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp-char",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp-char");

        var project = new PipelineProject
        {
            Id = "proj-3",
            Name = "DecompProject",
            Secrets = new Dictionary<string, string> { ["DECOMP_SECRET"] = "decomp-value" },
            SteeringContent = "decomp project steering"
        };

        SetupHappyPathMocks("agent-provider-1", steeringContent: "repo steering for decomp");

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchDecompositionToAgentAsync(
            agent, "epic-1", "Epic Title",
            PipelineRunType.DecompositionAnalysis,
            "ip", "rp", null, "user",
            Array.Empty<string>(), CancellationToken.None,
            decompositionSource: null,
            project: project);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();
        var msg = _capturedMessage!;

        // Shared properties
        msg.JobId.Should().NotBeNullOrEmpty();
        msg.IssueIdentifier.Should().Be("epic-1");
        msg.IssueDetail.Should().NotBeNull();
        msg.IssueDetail.Title.Should().Be("Epic Title");
        msg.IssueDetail.Description.Should().BeEmpty();
        msg.ParsedIssue.Should().NotBeNull();
        msg.IssueComments.Should().BeEmpty();
        msg.RepoProviderConfigId.Should().Be("rp");
        msg.AgentProviderConfigId.Should().Be("agent-provider-1");
        msg.BrainProviderConfigId.Should().BeNull();
        msg.PipelineProviderConfigId.Should().BeNull();
        msg.ProviderConfigs.Should().NotBeNull();
        msg.PipelineConfiguration.Should().NotBeNull();
        msg.InitiatedBy.Should().Be("user");
        msg.ResolvedProfileId.Should().Be("profile-1");
        msg.McpServers.Should().HaveCount(1);
        msg.ProjectId.Should().Be("proj-3");
        msg.ProjectName.Should().Be("DecompProject");
        msg.ProjectSecrets.Should().ContainKey("DECOMP_SECRET");
        msg.ProjectSteeringContent.Should().Be("decomp project steering");
        msg.RepoSteeringContent.Should().BeNull(); // TokenVendingService.CloneWithSettings does not copy SteeringContent
        msg.IssueProviderConfigId.Should().Be("ip");

        // Decomposition-specific properties
        msg.RunType.Should().Be(PipelineRunType.DecompositionAnalysis);
        msg.QualityGateConfigs.Should().BeEmpty();
        msg.ReviewerConfigs.Should().BeEmpty();
        msg.ProjectContext.Should().BeNull(); // No EpicIssueProviderId on project

        // Properties that should NOT be set for decomposition
        msg.ExistingAnalysis.Should().BeNull();
        msg.ForceRefreshAnalysis.Should().BeFalse();
        msg.LinkedPullRequest.Should().BeNull();
        msg.LinkedIssueContexts.Should().BeNull();
        msg.ReviewPrTargetBranch.Should().BeNull();
        msg.ReviewPrDescription.Should().BeNull();
        msg.ReviewPrAuthor.Should().BeNull();
    }

    [Fact]
    public async Task DispatchDecompositionToAgentAsync_Phase2_Characterization_AllPropertiesCorrectlyMapped()
    {
        // TODO: This test only asserts 5 properties (RunType, IssueIdentifier, IssueDetail.Title, QualityGateConfigs, ReviewerConfigs).
        // Add exhaustive assertions for shared fields (ProjectId, ProjectName, ProjectSecrets, McpServers, ProviderConfigs, etc.)
        // to match the other characterization tests and catch property-mapping drift bugs in the Phase 2 path.

        var agent = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-decomp-p2",
            Hostname = "host",
            Labels = new[] { "dotnet" }
        }, "conn-decomp-p2");

        var project = new PipelineProject
        {
            Id = "proj-4",
            Name = "DecompProject2",
            Secrets = new Dictionary<string, string> { ["SECRET"] = "val" },
            SteeringContent = "steering"
        };

        SetupHappyPathMocks("agent-provider-1");

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.DispatchDecompositionToAgentAsync(
            agent, "epic-2", "Epic Phase 2",
            PipelineRunType.Decomposition,
            "ip", "rp", null, "user",
            Array.Empty<string>(), CancellationToken.None,
            decompositionSource: "manual",
            project: project);

        result.Should().BeTrue();
        _capturedMessage.Should().NotBeNull();
        var msg = _capturedMessage!;

        // Verify Phase 2 RunType
        msg.RunType.Should().Be(PipelineRunType.Decomposition);
        msg.IssueIdentifier.Should().Be("epic-2");
        msg.IssueDetail.Title.Should().Be("Epic Phase 2");
        msg.QualityGateConfigs.Should().BeEmpty();
        msg.ReviewerConfigs.Should().BeEmpty();
    }

    #endregion

    // TODO: Add failure path characterization tests for BuildAndSendAsync and PrepareAndResolveConfigAsync.
    // Verify that exceptions from PrepareProviderConfigsAsync and PipelineConfigurationResolver.ResolveAsync
    // propagate correctly through the shared helpers and are caught by the try/catch in each dispatch method.
    // The issue prerequisites require "characterization tests covering dispatch success and failure paths".

    public void Dispose()
    {
        _httpClient.Dispose();
        foreach (var orchestration in _orchestrationInstances)
            orchestration.Dispose();
    }
}
