using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="CloneProjectReviewRepositoriesStep"/>: the project's other repositories are cloned for the
/// project reviewers, into the agent's metadata directory, and only when a code review with project reviewers runs.
/// </summary>
public class CloneProjectReviewRepositoriesStepTests : IDisposable
{
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"project-review-clone-{Guid.NewGuid():N}");

    public CloneProjectReviewRepositoriesStepTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private static readonly ReviewAgent ProjectReviewer = new() { Name = "ProjectReviewer", Prompt = "Check the project." };

    [Fact]
    public async Task ExecuteAsync_ProjectReviewers_ClonesIntoTheMetadataDirectoryAfterValidating()
    {
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        var context = BuildContext([ProjectReviewer], [api], [("api", provider.Object)]);

        var result = await new CloneProjectReviewRepositoriesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        api.LocalPath.Should().Be(".agent/project-repos/api");
        Directory.Exists(Path.Combine(_workspacePath, ".agent", "project-repos", "api")).Should().BeTrue();
        provider.Verify(p => p.ValidateAsync(It.IsAny<CancellationToken>()), Times.Once,
            "a GitLab repository learns its clone URL from validation");
        provider.Verify(p => p.CloneAsync(
            It.Is<WorkspacePath>(w => w.Value.EndsWith(Path.Combine(".agent", "project-repos", "api")) || w.Value.EndsWith(".agent/project-repos/api")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PrReviewRun_ClonesEvenWhenImplementationRunsHaveNoReview()
    {
        // CodeReview.MaxIterations = 0 turns off the review of implementation runs only
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        var context = BuildContext([ProjectReviewer], [api], [("api", provider.Object)],
            runType: PipelineRunType.Review, maxIterations: 0);

        await new CloneProjectReviewRepositoriesStep().ExecuteAsync(context, CancellationToken.None);

        provider.Verify(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<string> SkipCases => ["no project reviewers", "no repositories", "no providers", "implementation run without review"];

    [Theory]
    [MemberData(nameof(SkipCases))]
    public async Task ExecuteAsync_NothingToReview_ClonesNothing(string skipCase)
    {
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        var context = BuildContext(
            skipCase == "no project reviewers" ? [] : [ProjectReviewer],
            skipCase == "no repositories" ? [] : [api],
            skipCase == "no providers" ? null : [("api", provider.Object)],
            maxIterations: skipCase == "implementation run without review" ? 0 : 2);

        var result = await new CloneProjectReviewRepositoriesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        provider.Verify(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()), Times.Never);
        api.LocalPath.Should().BeNull();
        Directory.Exists(Path.Combine(_workspacePath, ".agent", "project-repos")).Should().BeFalse(
            "the cloner, which creates the folder first, never ran");
    }

    private PipelineStepContext BuildContext(
        IReadOnlyList<ReviewAgent> projectReviewers,
        IReadOnlyList<RepositoryTarget>? repositories,
        IReadOnlyList<(string TemplateName, IRepositoryProvider Provider)>? providers,
        PipelineRunType runType = PipelineRunType.Implementation,
        int maxIterations = 2)
    {
        return new PipelineStepContext
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "101",
                IssueTitle = "Add the order endpoint",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = runType,
                WorkspacePath = _workspacePath
            },
            Config = new PipelineConfiguration { CodeReview = new CodeReviewConfiguration { MaxIterations = maxIterations } },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = Mock.Of<IPipelineCallbacks>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectReviewers = projectReviewers,
            ProjectReviewRepositories = repositories,
            AdditionalRepoProviders = providers
        };
    }
}
