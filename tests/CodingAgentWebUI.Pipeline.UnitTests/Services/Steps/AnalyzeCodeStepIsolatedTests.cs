using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Isolated unit tests for <see cref="AnalyzeCodeStep"/> using mocked <see cref="IAgentPhaseExecutor"/>.
/// Decision (Issue #297): Approach 1 — interfaces already exist and are mockable.
/// </summary>
public class AnalyzeCodeStepIsolatedTests
{
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();
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
            CurrentStep = PipelineStep.AnalyzingCode,
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
            ProviderConfigStore = Mock.Of<IConfigurationStore>(),
            QualityGateConfigStore = Mock.Of<IConfigurationStore>(),
            ReviewerConfigStore = Mock.Of<IConfigurationStore>(),
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
    public async Task ExecuteAsync_WhenAnalysisReturnsTrue_ReturnsContinue()
    {
        _agentExecution
            .Setup(x => x.ExecuteAnalysisPhaseAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<IReadOnlyList<IssueComment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };

        var result = await new AnalyzeCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAnalysisReturnsFalse_ReturnsStop()
    {
        _agentExecution
            .Setup(x => x.ExecuteAnalysisPhaseAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<IReadOnlyList<IssueComment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };

        var result = await new AnalyzeCodeStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_PassesIssueCommentsToExecutor()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Author = "user", Body = "feedback", CreatedAt = DateTime.UtcNow }
        };

        _agentExecution
            .Setup(x => x.ExecuteAnalysisPhaseAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<IReadOnlyList<IssueComment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.IssueComments = comments;

        await new AnalyzeCodeStep().ExecuteAsync(context, CancellationToken.None);

        _agentExecution.Verify(x => x.ExecuteAnalysisPhaseAsync(
            It.IsAny<AgentPhaseContext>(),
            It.Is<IReadOnlyList<IssueComment>>(c => c.Count == 1 && c[0].Id == "c1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullIssueComments_PassesEmptyArray()
    {
        _agentExecution
            .Setup(x => x.ExecuteAnalysisPhaseAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<IReadOnlyList<IssueComment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };
        context.IssueComments = null;

        await new AnalyzeCodeStep().ExecuteAsync(context, CancellationToken.None);

        _agentExecution.Verify(x => x.ExecuteAnalysisPhaseAsync(
            It.IsAny<AgentPhaseContext>(),
            It.Is<IReadOnlyList<IssueComment>>(c => c.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
