using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Isolated unit tests for <see cref="ReviewCodeStep"/> using mocked <see cref="IAgentPhaseExecutor"/>.
/// Decision (Issue #297): Approach 1 — interfaces already exist and are mockable.
/// </summary>
public class ReviewCodeStepIsolatedTests
{
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    private PipelineStepContext BuildContext(PipelineRunType runType = PipelineRunType.Implementation)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.ReviewingCode,
            RepositoryName = "owner/repo",
            RunType = runType
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = _agentExecution.Object,
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    [Fact]
    public async Task ExecuteAsync_UsesPreResolvedReviewerConfigs_WhenSet()
    {
        var reviewers = new List<ReviewerConfiguration>
        {
            new() { DisplayName = "TestReviewer", Agents = [new ReviewAgent { Name = "R1", Prompt = "review" }] }
        };

        var context = BuildContext();
        context.Issue = new IssueDetail { IsAuthorVerified = true, Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = reviewers;

        await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        _agentExecution.Verify(x => x.ExecuteCodeReviewAsync(
            It.IsAny<AgentPhaseContext>(),
            It.IsAny<CancellationToken>(),
            It.Is<IReadOnlyList<ReviewerConfiguration>>(r => r.Count == 1 && r[0].DisplayName == "TestReviewer")),
            Times.Once);
        _configStore.Verify(x => x.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectReviewers_JoinTheOthersAndAreKeptForTheirRetries()
    {
        // The project reviewers run in the same review as the label reviewers, so they run concurrently with them
        var context = BuildContext();
        context.Issue = new IssueDetail { IsAuthorVerified = true, Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs =
        [
            new ReviewerConfiguration { DisplayName = "Default Reviewers", Agents = [new ReviewAgent { Name = "Correctness", Prompt = "review" }] }
        ];
        context.ProjectReviewers = [new ReviewAgent { Name = "ProjectReviewer", Prompt = "Check the project." }];
        context.ProjectReviewRepositories =
            [new RepositoryTarget { TemplateName = "web", Description = "", RepoProviderId = "repo-web", LocalPath = ".agent/project-repos/web" }];
        IReadOnlyList<ReviewerConfiguration>? passed = null;
        _agentExecution
            .Setup(x => x.ExecuteCodeReviewAsync(It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<ReviewerConfiguration>?>()))
            .Callback<AgentPhaseContext, CancellationToken, IReadOnlyList<ReviewerConfiguration>?>((_, _, r) => passed = r)
            .Returns(Task.CompletedTask);

        await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        ReviewerResolver.FlattenAgents(passed).Select(a => a.Name).Should().Equal("Correctness", "ProjectReviewer");
        passed![1].Agents[0].Prompt.Should().Contain("`.agent/project-repos/web/`");
        context.ResolvedReviewerConfigs.Should().BeSameAs(passed, "PostReviewFindingsStep retries a reviewer by its configuration");
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysReturnsContinue()
    {
        var context = BuildContext();
        context.Issue = new IssueDetail { IsAuthorVerified = true, Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = [];

        var result = await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRunAllAgentsFailed_FailsRunAndStops()
    {
        var context = BuildAllAgentsFailedContext(PipelineRunType.Review);

        var result = await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop, "PostReviewFindingsStep must not post a review that never happened");
        context.Run.FailureReason.Should().Be("All review agents failed; no review was posted.");
        context.Run.FinalLabel.Should().Be(AgentLabels.Error);
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRunWithoutAllAgentsFailed_ContinuesAndDoesNotFailRun()
    {
        var context = BuildContext(PipelineRunType.Review);
        context.Issue = new IssueDetail { IsAuthorVerified = true, Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = [];

        var result = await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "a review run whose reviewers did not all crash posts its review as before");
        context.Run.FailureReason.Should().BeNull();
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ImplementationRunAllAgentsFailed_Continues()
    {
        var context = BuildAllAgentsFailedContext(PipelineRunType.Implementation);

        var result = await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue, "the quality gates of an implementation run still decide the outcome");
        context.Run.FailureReason.Should().BeNull();
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
    }

    /// <summary>A context whose code review reports that every review agent failed.</summary>
    private PipelineStepContext BuildAllAgentsFailedContext(PipelineRunType runType)
    {
        var context = BuildContext(runType);
        context.Issue = new IssueDetail { IsAuthorVerified = true, Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = [];
        _agentExecution
            .Setup(x => x.ExecuteCodeReviewAsync(It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<ReviewerConfiguration>?>()))
            .Callback<AgentPhaseContext, CancellationToken, IReadOnlyList<ReviewerConfiguration>?>((phase, _, _) => phase.Run.CodeReviewAllAgentsFailed = true)
            .Returns(Task.CompletedTask);
        return context;
    }
}
