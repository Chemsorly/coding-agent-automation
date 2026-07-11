using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

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

    private PipelineStepContext BuildContext()
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
            RepositoryName = "owner/repo"
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
            ProviderConfigStore = _configStore.Object,
            QualityGateConfigStore = _configStore.Object,
            ReviewerConfigStore = _configStore.Object,
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
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
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
    public async Task ExecuteAsync_ResolvesFromConfigStore_WhenPreResolvedIsNull()
    {
        _configStore.Setup(x => x.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());
        _configStore.Setup(x => x.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = null;

        await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        _configStore.Verify(x => x.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysReturnsContinue()
    {
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.PreResolvedReviewerConfigs = [];

        var result = await new ReviewCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }
}
