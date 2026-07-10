using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;
// ReSharper disable InconsistentNaming

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchOrchestrationService"/>.
/// Verifies profile resolution, label swap, run creation, and provider config prep.
/// </summary>
public class DispatchOrchestrationServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly OrchestratorRunService _runService;
    private readonly DispatchResolutionService _resolution;

    private static readonly AgentProfile TestProfile = new()
    {
        Id = "profile-1",
        DisplayName = "Test Profile",
        AgentProviderConfigId = "agent-config-1",
        Enabled = true,
        MatchLabels = ["dotnet"],
        McpServers = []
    };

    private static readonly PipelineConfiguration TestConfig = new()
    {
        WorkspaceBaseDirectory = "/tmp/workspace"
    };

    private static readonly PipelineProject TestProject = new()
    {
        Id = "proj-1",
        Name = "TestProject",
        Enabled = true
    };

    private static readonly ProviderConfig TestRepoConfig = new()
    {
        Id = "repo-1",
        DisplayName = "Repo",
        ProviderType = "github",
        Kind = ProviderKind.Repository
    };

    private static readonly ProviderConfig TestAgentConfig = new()
    {
        Id = "agent-config-1",
        DisplayName = "Agent",
        ProviderType = "kiro",
        Kind = ProviderKind.Agent
    };

    public DispatchOrchestrationServiceTests()
    {
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            _mockLogger.Object);
    }

    private DispatchOrchestrationService CreateService(
        PipelineOrchestrationService orchestration)
    {
        return new DispatchOrchestrationService(
            new DispatchInfrastructure(
                _mockTokenVending.Object,
                _mockProviderFactory.Object,
                _mockLabelSwapper.Object,
                _resolution),
            orchestration,
            _runService,
            _mockWorkDistributor.Object,
            _mockLogger.Object);
    }

    private PipelineOrchestrationService CreateOrchestration()
    {
        // Setup provider factory to return dummy providers
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.RepositoryFullName).Returns("owner/repo");
        _mockProviderFactory
            .Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);

        return TestUtilities.TestOrchestrationFactory.CreateMinimal(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            logger: _mockLogger.Object,
            runService: _runService);
    }

    private void SetupStandardMocks()
    {
        _mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { TestProfile });

        _mockConfigStore
            .Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestConfig);

        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestRepoConfig);

        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("agent-config-1", ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestAgentConfig);

        _mockConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { TestRepoConfig });

        _mockConfigStore
            .Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { TestAgentConfig });

        _mockConfigStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        // Issue provider mock
        var issueConfig = new ProviderConfig
        {
            Id = "issue-1",
            DisplayName = "Issue Provider",
            ProviderType = "github",
            Kind = ProviderKind.Issue
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("issue-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync("issue-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "issue-42",
                Title = "Test Issue Title",
                Description = "## Requirements\nDo the thing",
                Labels = ["agent:next"]
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync("issue-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        // Token vending pass-through
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .Returns<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => Task.FromResult(configs));
    }

    [Fact]
    public async Task PrepareAsync_WithValidInputs_ReturnsResult()
    {
        SetupStandardMocks();
        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().NotBeNull();
        result!.ResolvedProfile.Id.Should().Be("profile-1");
        result.IssueDetail.Title.Should().Be("Test Issue Title");
        result.CreatedRun.Should().NotBeNull();
        result.CreatedRun.IssueIdentifier.Should().Be("issue-42");
        result.Project.Id.Should().Be("proj-1");
        result.PipelineConfiguration.Should().NotBeNull();
    }

    [Fact]
    public async Task PrepareAsync_DoesNotSwapLabel()
    {
        SetupStandardMocks();
        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        // Label swap is deferred to ConfirmDistributionLabelAsync (#997)
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ConfirmDistributionLabelAsync_SwapsLabelToInProgress()
    {
        SetupStandardMocks();
        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "issue-42",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        };

        await service.ConfirmDistributionLabelAsync(request, CancellationToken.None);

        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync("issue-1", "issue-42", AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PrepareAsync_WhenNoProfileMatches_ReturnsNull()
    {
        SetupStandardMocks();
        // Override profiles to return empty (no match)
        _mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_WhenIssueProviderNotFound_ReturnsNull()
    {
        SetupStandardMocks();
        // Override to return null for issue config
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("issue-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_CreatesRunViaOrchestrationService()
    {
        SetupStandardMocks();
        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CreatedRun.IssueIdentifier.Should().Be("issue-42");
        result.CreatedRun.ProjectId.Should().Be("proj-1");
        result.CreatedRun.ProjectName.Should().Be("TestProject");
        // Run should be tracked
        _runService.GetRun(result.CreatedRun.RunId).Should().NotBeNull();
    }

    [Fact]
    public async Task PrepareAsync_WhenExceptionThrown_ReturnsNull()
    {
        SetupStandardMocks();
        // Make issue provider throw
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Throws(new InvalidOperationException("Provider crashed"));

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_IncludesProviderConfigs()
    {
        SetupStandardMocks();
        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);

        var result = await service.PrepareAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            requiredLabels: ["dotnet"],
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().NotBeNull();
        result!.ProviderConfigs.Should().NotBeEmpty();
        result.ProviderConfigs.Should().Contain(c => c.Id == "repo-1");
        result.ProviderConfigs.Should().Contain(c => c.Id == "agent-config-1");
    }

    // ── IDispatchOrchestrationService interface method tests ───────────────────

    [Fact]
    public async Task PrepareDistributionRequestAsync_ReturnsJobDistributionRequest_WithCorrectFields()
    {
        SetupStandardMocks();
        // Setup repo config with RequiredLabels so LabelResolver picks them up
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var request = await iface.PrepareDistributionRequestAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "test-user",
            project: TestProject,
            taskType: WorkItemTaskType.Implementation,
            runType: PipelineRunType.Implementation,
            ct: CancellationToken.None);

        request.Should().NotBeNull();
        request!.IssueIdentifier.Should().Be("issue-42");
        request.RepoProviderConfigId.Should().Be("repo-1");
        request.InitiatedBy.Should().Be("test-user");
        request.TaskType.Should().Be(WorkItemTaskType.Implementation);
        request.RunType.Should().Be(PipelineRunType.Implementation);
        request.ProjectId.Should().Be("proj-1");
        request.ProjectName.Should().Be("TestProject");
        request.ResolvedProfileId.Should().Be("profile-1");
        request.IssueDetail.Should().NotBeNull();
        request.ProviderConfigs.Should().NotBeNullOrEmpty();
        request.PipelineConfiguration.Should().NotBeNull();
    }

    [Fact]
    public async Task PrepareDistributionRequestAsync_SetsAgentSelector_AsSortedCommaJoinedLabels()
    {
        SetupStandardMocks();
        // Profile with multiple match labels in unsorted order
        var multiLabelProfile = new AgentProfile
        {
            Id = "profile-multi",
            DisplayName = "Multi Label Profile",
            AgentProviderConfigId = "agent-config-1",
            Enabled = true,
            MatchLabels = ["python", "dotnet", "aws"],
            McpServers = []
        };
        _mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { multiLabelProfile });

        // Repo config returns all three labels
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["python", "dotnet", "aws"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var request = await iface.PrepareDistributionRequestAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            project: TestProject,
            ct: CancellationToken.None);

        request.Should().NotBeNull();
        // Labels sorted alphabetically: aws, dotnet, python
        request!.AgentSelector.Should().Be("aws,dotnet,python");
    }

    [Fact]
    public async Task PrepareDistributionRequestAsync_WhenNoMatchingProfile_ReturnsNull()
    {
        SetupStandardMocks();
        _mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        // Repo config with labels that won't match any profile
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["java"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var result = await iface.PrepareDistributionRequestAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareReviewDistributionRequestAsync_IncludesReviewSpecificFields()
    {
        SetupStandardMocks();
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var reviewRequest = new ReviewDispatchRequest
        {
            PrIdentifier = "issue-42",
            PrBranchName = "feature/my-pr",
            PrTitle = "My PR",
            PrDescription = "This PR does things",
            PrAuthor = "dev-user",
            PrUrl = "https://github.com/org/repo/pull/42",
            PrTargetBranch = "main",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            BrainProviderId = null,
            InitiatedBy = "review-loop"
        };

        var result = await iface.PrepareReviewDistributionRequestAsync(
            reviewRequest, TestProject, CancellationToken.None);

        result.Should().NotBeNull();
        result!.TaskType.Should().Be(WorkItemTaskType.Review);
        result.RunType.Should().Be(PipelineRunType.Review);
        result.LinkedPullRequest.Should().NotBeNull();
        result.LinkedPullRequest!.Url.Should().Be("https://github.com/org/repo/pull/42");
        result.LinkedPullRequest.BranchName.Should().Be("feature/my-pr");
        result.ReviewPrTargetBranch.Should().Be("main");
        result.ReviewPrDescription.Should().Be("This PR does things");
        result.ReviewPrAuthor.Should().Be("dev-user");
    }

    [Fact]
    public async Task PrepareReviewDistributionRequestAsync_WhenOrchestrationFails_ReturnsNull()
    {
        SetupStandardMocks();
        // No profiles → orchestration fails
        _mockConfigStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var reviewRequest = new ReviewDispatchRequest
        {
            PrIdentifier = "issue-42",
            PrBranchName = "feature/x",
            PrTitle = "X",
            PrUrl = "https://github.com/org/repo/pull/42",
            PrTargetBranch = "main",
            IssueProviderId = "issue-1",
            RepoProviderId = "repo-1",
            InitiatedBy = "loop"
        };

        var result = await iface.PrepareReviewDistributionRequestAsync(
            reviewRequest, TestProject, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareDecompositionDistributionRequestAsync_ReturnsRequestWithDecompositionFields()
    {
        SetupStandardMocks();
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        // Mock issue provider for the epic
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync("epic-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "epic-1",
                Title = "Epic: Build the thing",
                Description = "## Requirements\nBuild all the things",
                Labels = ["agent:next"]
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync("epic-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var result = await iface.PrepareDecompositionDistributionRequestAsync(
            epicIdentifier: "epic-1",
            epicTitle: "Epic: Build the thing",
            phaseType: PipelineRunType.Decomposition,
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            initiatedBy: "decomp-loop",
            project: TestProject,
            decompositionSource: "https://github.com/org/repo/issues/100",
            ct: CancellationToken.None);

        result.Should().NotBeNull();
        result!.TaskType.Should().Be(WorkItemTaskType.Decomposition);
        result.RunType.Should().Be(PipelineRunType.Decomposition);
        result.DecompositionSource.Should().Be("https://github.com/org/repo/issues/100");
        result.IssueIdentifier.Should().Be("epic-1");
    }

    [Fact]
    public async Task PrepareDecompositionDistributionRequestAsync_WhenOrchestrationFails_ReturnsNull()
    {
        SetupStandardMocks();
        // Issue provider throws
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Throws(new InvalidOperationException("boom"));

        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var result = await iface.PrepareDecompositionDistributionRequestAsync(
            epicIdentifier: "epic-1",
            epicTitle: "Epic Title",
            phaseType: PipelineRunType.Decomposition,
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            initiatedBy: "loop",
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task PrepareDistributionRequestAsync_ResolvesLabelsFromRepoConfig()
    {
        SetupStandardMocks();
        // Setup repo config that uses RequiredLabels property
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var result = await iface.PrepareDistributionRequestAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            project: TestProject,
            ct: CancellationToken.None);

        // Profile with ["dotnet"] matches the resolved labels
        result.Should().NotBeNull();
        result!.ResolvedProfileId.Should().Be("profile-1");
        result.AgentSelector.Should().Be("dotnet");
    }

    [Fact]
    public async Task PrepareDistributionRequestAsync_RunTrackedByOrchestratorRunService()
    {
        SetupStandardMocks();
        var repoConfigWithLabels = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo",
            ProviderType = "github",
            Kind = ProviderKind.Repository,
            RequiredLabels = ["dotnet"]
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigWithLabels);

        var orchestration = CreateOrchestration();
        var service = CreateService(orchestration);
        IDispatchOrchestrationService iface = service;

        var result = await iface.PrepareDistributionRequestAsync(
            issueIdentifier: "issue-42",
            issueProviderId: "issue-1",
            repoProviderId: "repo-1",
            brainProviderId: null,
            pipelineProviderId: null,
            initiatedBy: "loop",
            project: TestProject,
            ct: CancellationToken.None);

        result.Should().NotBeNull();
        // The run should be registered in OrchestratorRunService
        var activeRuns = _runService.GetActiveRuns();
        activeRuns.Should().Contain(r => r.IssueIdentifier == "issue-42");
    }
}

/// <summary>
/// Tests for <see cref="DispatchOrchestrationService.RevertFailedDistributionAsync"/>.
/// Validates that distribution failure cleanup reverts the label and removes the dangling run.
/// </summary>
public class DispatchOrchestrationService_RevertFailedDistributionTests
{
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly OrchestratorRunService _runService;
    private readonly DispatchOrchestrationService _service;

    public DispatchOrchestrationService_RevertFailedDistributionTests()
    {
        _runService = new OrchestratorRunService(_mockLogger.Object);

        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockProviderFactory = new Mock<IProviderFactory>();
        var mockTokenVending = new Mock<ITokenVendingService>();
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            mockConfigStore.Object,
            _mockLogger.Object);
        var orchestration = TestUtilities.TestOrchestrationFactory.CreateMinimal(
            configStore: mockConfigStore.Object,
            providerFactory: mockProviderFactory.Object,
            logger: _mockLogger.Object,
            runService: _runService);

        _service = new DispatchOrchestrationService(
            new DispatchInfrastructure(
                mockTokenVending.Object, mockProviderFactory.Object,
                _mockLabelSwapper.Object, resolution),
            orchestration,
            _runService,
            new Mock<IWorkDistributor>().Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task RevertFailedDistribution_SwapsLabelBackToNext()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#10",
            IssueProviderConfigId = "ipc-1",
            RepoProviderConfigId = "rpc-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        };

        await _service.RevertFailedDistributionAsync(request, CancellationToken.None);

        _mockLabelSwapper.Verify(
            s => s.SwapLabelAsync("ipc-1", "owner/repo#10", AgentLabels.Next, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RevertFailedDistribution_RemovesDanglingRun()
    {
        // Arrange: simulate a dangling run
        var run = new PipelineRun
        {
            RunId = "run-abc",
            IssueIdentifier = "owner/repo#20",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ipc-2",
            RepoProviderConfigId = "rpc-2"
        };
        _runService.AddRun(run);

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#20",
            IssueProviderConfigId = "ipc-2",
            RepoProviderConfigId = "rpc-2",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        };

        // Act
        await _service.RevertFailedDistributionAsync(request, CancellationToken.None);

        // Assert: run removed
        _runService.GetActiveRuns().Should().NotContain(r => r.IssueIdentifier == "owner/repo#20");
    }

    [Fact]
    public async Task RevertFailedDistribution_LabelSwapFailure_DoesNotThrow()
    {
        _mockLabelSwapper
            .Setup(s => s.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unreachable"));

        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#30",
            IssueProviderConfigId = "ipc-3",
            RepoProviderConfigId = "rpc-3",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        };

        // Should not throw
        await _service.RevertFailedDistributionAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task RevertFailedDistribution_NoMatchingRun_DoesNotThrow()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#999",
            IssueProviderConfigId = "ipc-nonexistent",
            RepoProviderConfigId = "rpc-x",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        };

        // Should not throw even with no matching run
        await _service.RevertFailedDistributionAsync(request, CancellationToken.None);

        _mockLabelSwapper.Verify(
            s => s.SwapLabelAsync("ipc-nonexistent", "owner/repo#999", AgentLabels.Next, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

/// <summary>
/// Tests for <see cref="DispatchOrchestrationService.DistributeAndFinalizeAsync"/>.
/// Validates the unified distribute → confirm/revert lifecycle.
/// </summary>
public class DispatchOrchestrationService_DistributeAndFinalizeTests
{
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly OrchestratorRunService _runService;
    private readonly DispatchOrchestrationService _service;

    private static readonly JobDistributionRequest TestRequest = new()
    {
        IssueIdentifier = "owner/repo#42",
        IssueProviderConfigId = "ipc-1",
        RepoProviderConfigId = "rpc-1",
        InitiatedBy = "loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet",
        TimeoutSeconds = 3600
    };

    public DispatchOrchestrationService_DistributeAndFinalizeTests()
    {
        _runService = new OrchestratorRunService(_mockLogger.Object);

        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockProviderFactory = new Mock<IProviderFactory>();
        var mockTokenVending = new Mock<ITokenVendingService>();
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            mockConfigStore.Object,
            _mockLogger.Object);
        var orchestration = TestUtilities.TestOrchestrationFactory.CreateMinimal(
            configStore: mockConfigStore.Object,
            providerFactory: mockProviderFactory.Object,
            logger: _mockLogger.Object,
            runService: _runService);

        _service = new DispatchOrchestrationService(
            new DispatchInfrastructure(
                mockTokenVending.Object, mockProviderFactory.Object,
                _mockLabelSwapper.Object, resolution),
            orchestration,
            _runService,
            _mockWorkDistributor.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task DistributeAndFinalizeAsync_WhenDistributeSucceeds_ConfirmsLabel()
    {
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "work-1", null, Queued: false));

        var outcome = await _service.DistributeAndFinalizeAsync(TestRequest, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Queued.Should().BeFalse();
        outcome.ErrorMessage.Should().BeNull();

        // Confirm label was swapped to agent:in-progress
        _mockLabelSwapper.Verify(
            s => s.SwapLabelAsync("ipc-1", "owner/repo#42", AgentLabels.InProgress, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // TODO: Add a test that pre-registers a PipelineRun in OrchestratorRunService and asserts it is
    // removed after DistributeAndFinalizeAsync failure. Currently this test only verifies the label
    // swap to agent:next but does not exercise the dangling run-removal branch of RevertFailedDistributionAsync.
    [Fact]
    public async Task DistributeAndFinalizeAsync_WhenDistributeFails_RevertsDistribution()
    {
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "No agent available"));

        var outcome = await _service.DistributeAndFinalizeAsync(TestRequest, CancellationToken.None);

        outcome.Success.Should().BeFalse();
        outcome.Queued.Should().BeFalse();
        outcome.ErrorMessage.Should().Be("No agent available");

        // Label should be reverted to agent:next
        _mockLabelSwapper.Verify(
            s => s.SwapLabelAsync("ipc-1", "owner/repo#42", AgentLabels.Next, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DistributeAndFinalizeAsync_WhenDistributeSucceedsAndQueued_DoesNotConfirmLabel()
    {
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "work-1", null, Queued: true));

        var outcome = await _service.DistributeAndFinalizeAsync(TestRequest, CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.Queued.Should().BeTrue();
        outcome.ErrorMessage.Should().BeNull();

        // No label swap should have occurred (drain service handles it later)
        _mockLabelSwapper.Verify(
            s => s.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DistributeAndFinalizeAsync_PassesCancellationTokenToDistributor()
    {
        using var cts = new CancellationTokenSource();
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), cts.Token))
            .ReturnsAsync(new DistributionResult(true, "work-1", null));

        await _service.DistributeAndFinalizeAsync(TestRequest, cts.Token);

        _mockWorkDistributor.Verify(
            w => w.DistributeAsync(TestRequest, cts.Token),
            Times.Once);
    }
}
