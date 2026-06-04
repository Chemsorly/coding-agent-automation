using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="CloneProjectRepositoriesStep"/>.
/// Verifies parallel cloning of additional project repos for cross-repo decomposition.
/// </summary>
public class CloneProjectRepositoriesStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public CloneProjectRepositoriesStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"clone-proj-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_NullProjectContext_SkipsAndReturnsContinue()
    {
        // Arrange — per-template decomposition (no project context)
        var context = BuildContext(projectContext: null, additionalProviders: null);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_NullAdditionalProviders_SkipsAndReturnsContinue()
    {
        // Arrange — project context exists but no additional providers resolved
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [new RepositoryTarget { TemplateName = "primary", Description = "Primary repo" }]
        };
        var context = BuildContext(projectContext, additionalProviders: null);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAdditionalProviders_SkipsAndReturnsContinue()
    {
        // Arrange — additional providers list is empty
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [new RepositoryTarget { TemplateName = "primary", Description = "Primary repo" }]
        };
        var context = BuildContext(projectContext, additionalProviders: []);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulClone_SetsLocalPathOnRepositoryTarget()
    {
        // Arrange
        var backendTarget = new RepositoryTarget
        {
            TemplateName = "backend-api",
            Description = "Backend REST API",
            RepoProviderId = "provider-backend"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [backendTarget]
        };

        var mockProvider = new Mock<IRepositoryProvider>();
        mockProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(projectContext, [("backend-api", mockProvider.Object)]);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        backendTarget.LocalPath.Should().Be("repos/backend-api");
        mockProvider.Verify(p => p.CloneAsync(
            It.Is<string>(path => path.EndsWith("backend-api")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleRepos_ClonesAllInParallel()
    {
        // Arrange
        var backendTarget = new RepositoryTarget
        {
            TemplateName = "backend-api",
            Description = "Backend",
            RepoProviderId = "p1"
        };
        var frontendTarget = new RepositoryTarget
        {
            TemplateName = "frontend-web",
            Description = "Frontend",
            RepoProviderId = "p2"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [backendTarget, frontendTarget]
        };

        var backendProvider = new Mock<IRepositoryProvider>();
        backendProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var frontendProvider = new Mock<IRepositoryProvider>();
        frontendProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(projectContext, [
            ("backend-api", backendProvider.Object),
            ("frontend-web", frontendProvider.Object)
        ]);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        backendTarget.LocalPath.Should().Be("repos/backend-api");
        frontendTarget.LocalPath.Should().Be("repos/frontend-web");
        backendProvider.Verify(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        frontendProvider.Verify(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CloneFailure_NonCritical_ReturnsContinue()
    {
        // Arrange — clone throws but step should not fail the pipeline
        var target = new RepositoryTarget
        {
            TemplateName = "failing-repo",
            Description = "This will fail",
            RepoProviderId = "p-fail"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [target]
        };

        var failingProvider = new Mock<IRepositoryProvider>();
        failingProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var context = BuildContext(projectContext, [("failing-repo", failingProvider.Object)]);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — non-critical: returns Continue, LocalPath stays null
        result.Should().Be(StepResult.Continue);
        target.LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_MixedSuccessAndFailure_SetsLocalPathOnlyForSuccessful()
    {
        // Arrange
        var successTarget = new RepositoryTarget
        {
            TemplateName = "good-repo",
            Description = "Will succeed",
            RepoProviderId = "p-good"
        };
        var failTarget = new RepositoryTarget
        {
            TemplateName = "bad-repo",
            Description = "Will fail",
            RepoProviderId = "p-bad"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [successTarget, failTarget]
        };

        var goodProvider = new Mock<IRepositoryProvider>();
        goodProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var badProvider = new Mock<IRepositoryProvider>();
        badProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Auth failed"));

        var context = BuildContext(projectContext, [
            ("good-repo", goodProvider.Object),
            ("bad-repo", badProvider.Object)
        ]);
        var step = new CloneProjectRepositoriesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        successTarget.LocalPath.Should().Be("repos/good-repo");
        failTarget.LocalPath.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_CreatesReposDirectory()
    {
        // Arrange
        var target = new RepositoryTarget
        {
            TemplateName = "some-repo",
            Description = "Repo",
            RepoProviderId = "p1"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [target]
        };

        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(projectContext, [("some-repo", provider.Object)]);
        var step = new CloneProjectRepositoriesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — repos directory was created
        Directory.Exists(Path.Combine(_workspacePath, "repos")).Should().BeTrue();
        Directory.Exists(Path.Combine(_workspacePath, "repos", "some-repo")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_PipelineCancellation_PropagatesCancellation()
    {
        // Arrange
        var target = new RepositoryTarget
        {
            TemplateName = "slow-repo",
            Description = "Slow",
            RepoProviderId = "p-slow"
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = [target]
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled

        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(projectContext, [("slow-repo", provider.Object)]);
        var step = new CloneProjectRepositoriesStep();

        // Act & Assert — should propagate cancellation
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => step.ExecuteAsync(context, cts.Token));
    }

    #region Helpers

    private PipelineStepContext BuildContext(
        DecompositionProjectContext? projectContext,
        IReadOnlyList<(string TemplateName, IRepositoryProvider Provider)>? additionalProviders)
    {
        return new PipelineStepContext
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "100",
                IssueTitle = "Test Epic",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = PipelineRunType.DecompositionAnalysis,
                WorkspacePath = _workspacePath
            },
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 10 },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectContext = projectContext,
            AdditionalRepoProviders = additionalProviders
        };
    }

    #endregion
}
