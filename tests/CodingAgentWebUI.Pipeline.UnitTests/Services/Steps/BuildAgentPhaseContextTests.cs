using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Unit tests for <see cref="PipelineStepContext.BuildAgentPhaseContext"/>.
/// Validates: Requirements 26.2
/// </summary>
public class BuildAgentPhaseContextTests
{
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    private PipelineStepContext BuildContext()
    {
        var run = new PipelineRun
        {
            RunId = "test-run-id",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-config",
            RepoProviderConfigId = "repo-config",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Created,
            RepositoryName = "owner/repo"
        };

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var prOrchestrator = new PullRequestOrchestrator(_logger);

        return new PipelineStepContext
        {
            Run = run,
            Config = config,
            RepoProvider = _repoProvider.Object,
            AgentProvider = _agentProvider.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            IssueProvider = null,
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = new AgentPhaseExecutor(_logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, new CiLogWriter(_logger), new FeedbackService(_logger), _logger),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = _logger
        };
    }

    [Fact]
    public void BuildAgentPhaseContext_NullIssue_ThrowsInvalidOperationException()
    {
        var context = BuildContext();
        context.Issue = null;
        context.ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = [] };

        var act = () => context.BuildAgentPhaseContext();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Issue*");
    }

    [Fact]
    public void BuildAgentPhaseContext_NullParsedIssue_ThrowsInvalidOperationException()
    {
        var context = BuildContext();
        context.Issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        context.ParsedIssue = null;

        var act = () => context.BuildAgentPhaseContext();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ParsedIssue*");
    }

    [Fact]
    public void BuildAgentPhaseContext_ValidState_ReturnsCorrectContext()
    {
        var context = BuildContext();
        var issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = ["bug"] };
        var parsedIssue = new ParsedIssue { RequirementsSection = "requirements", AcceptanceCriteria = ["AC1", "AC2"] };
        context.Issue = issue;
        context.ParsedIssue = parsedIssue;

        var result = context.BuildAgentPhaseContext();

        result.Should().NotBeNull();
        result.Run.Should().BeSameAs(context.Run);
        result.Config.Should().BeSameAs(context.Config);
        result.AgentProvider.Should().BeSameAs(context.AgentProvider);
        result.IssueOps.Should().BeSameAs(context.IssueOps);
        result.Callbacks.Should().BeSameAs(context.Callbacks);
        result.OrchestratorCts.Should().BeSameAs(context.Cts);
        result.Issue.Should().BeSameAs(issue);
        result.ParsedIssue.Should().BeSameAs(parsedIssue);
    }
}
