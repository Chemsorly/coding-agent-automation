using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Characterization tests for <see cref="PipelineStepContext.ForOrchestrator"/> and
/// <see cref="PipelineStepContext.ForAgent"/> factory methods.
/// Verifies that all parameters are correctly forwarded and site-specific properties are set.
/// Uses non-null/non-default values for all parameters to detect behavioral divergences.
/// </summary>
// TODO: Add a regression test (or architectural constraint) that verifies call sites
// (PipelineOrchestrationService, LocalPipelineExecutor) actually use the factory methods
// rather than direct object initialization, to prevent silent reversion of the refactoring.
public class PipelineStepContextFactoryTests
{
    private readonly PipelineRun _run = new()
    {
        RunId = "run-42",
        IssueIdentifier = "issue-7",
        IssueTitle = "Factory Test Issue",
        IssueProviderConfigId = "ip-config",
        RepoProviderConfigId = "rp-config",
        StartedAt = DateTime.UtcNow,
        CurrentStep = PipelineStep.Created,
        RepositoryName = "owner/repo"
    };

    private readonly PipelineConfiguration _config = new()
    {
        WorkspaceBaseDirectory = "/tmp/test-workspace"
    };

    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IRepositoryProvider> _brainProvider = new();
    private readonly Mock<IPipelineProvider> _pipelineProvider = new();
    // TODO: Implement IDisposable on this test class and dispose _cts to avoid leaking OS timer handles in large test runs.
    private readonly CancellationTokenSource _cts = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();
    private readonly Mock<IQualityGateExecutor> _qualityGates = new();
    private readonly Mock<IBrainSyncService> _brainSync = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly Mock<IQualityGateValidator> _qualityGateValidator = new();
    private readonly Mock<IIssueProvider> _issueProvider = new();

    public PipelineStepContextFactoryTests()
    {
        _prOrchestrator = new PullRequestOrchestrator(_logger);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ForOrchestrator tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForOrchestrator_SetsAllSharedProperties_Correctly()
    {
        var ctx = PipelineStepContext.ForOrchestrator(
            run: _run,
            config: _config,
            repoProvider: _repoProvider.Object,
            agentProvider: _agentProvider.Object,
            brainProvider: _brainProvider.Object,
            pipelineProvider: _pipelineProvider.Object,
            cts: _cts,
            configStore: _configStore.Object,
            callbacks: _callbacks.Object,
            issueOps: _issueOps.Object,
            agentExecution: _agentExecution.Object,
            qualityGates: _qualityGates.Object,
            brainSync: _brainSync.Object,
            prOrchestrator: _prOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator.Object,
            issueProvider: _issueProvider.Object);

        ctx.Run.Should().BeSameAs(_run);
        ctx.Config.Should().BeSameAs(_config);
        ctx.RepoProvider.Should().BeSameAs(_repoProvider.Object);
        ctx.AgentProvider.Should().BeSameAs(_agentProvider.Object);
        ctx.BrainProvider.Should().BeSameAs(_brainProvider.Object);
        ctx.PipelineProvider.Should().BeSameAs(_pipelineProvider.Object);
        ctx.Cts.Should().BeSameAs(_cts);
        ctx.ConfigStore.Should().BeSameAs(_configStore.Object);
        ctx.Callbacks.Should().BeSameAs(_callbacks.Object);
        ctx.IssueOps.Should().BeSameAs(_issueOps.Object);
        ctx.AgentExecution.Should().BeSameAs(_agentExecution.Object);
        ctx.QualityGates.Should().BeSameAs(_qualityGates.Object);
        ctx.BrainSync.Should().BeSameAs(_brainSync.Object);
        ctx.PrOrchestrator.Should().BeSameAs(_prOrchestrator);
        ctx.Logger.Should().BeSameAs(_logger);
        ctx.QualityGateValidator.Should().BeSameAs(_qualityGateValidator.Object);
    }

    [Fact]
    public void ForOrchestrator_SetsIssueProvider()
    {
        var ctx = BuildOrchestratorContext();

        ctx.IssueProvider.Should().BeSameAs(_issueProvider.Object);
    }

    [Fact]
    public void ForOrchestrator_DoesNotSetAgentSpecificProperties()
    {
        var ctx = BuildOrchestratorContext();

        ctx.Issue.Should().BeNull();
        ctx.ParsedIssue.Should().BeNull();
        ctx.IssueComments.Should().BeNull();
        ctx.PreResolvedReviewerConfigs.Should().BeNull();
        ctx.PreResolvedQualityGateConfigs.Should().BeNull();
        ctx.ProjectContext.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ForAgent tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForAgent_SetsAllSharedProperties_Correctly()
    {
        var ctx = BuildAgentContext();

        ctx.Run.Should().BeSameAs(_run);
        ctx.Config.Should().BeSameAs(_config);
        ctx.RepoProvider.Should().BeSameAs(_repoProvider.Object);
        ctx.AgentProvider.Should().BeSameAs(_agentProvider.Object);
        ctx.BrainProvider.Should().BeSameAs(_brainProvider.Object);
        ctx.PipelineProvider.Should().BeSameAs(_pipelineProvider.Object);
        ctx.Cts.Should().BeSameAs(_cts);
        ctx.ConfigStore.Should().BeSameAs(_configStore.Object);
        ctx.Callbacks.Should().BeSameAs(_callbacks.Object);
        ctx.IssueOps.Should().BeSameAs(_issueOps.Object);
        ctx.AgentExecution.Should().BeSameAs(_agentExecution.Object);
        ctx.QualityGates.Should().BeSameAs(_qualityGates.Object);
        ctx.BrainSync.Should().BeSameAs(_brainSync.Object);
        ctx.PrOrchestrator.Should().BeSameAs(_prOrchestrator);
        ctx.Logger.Should().BeSameAs(_logger);
        ctx.QualityGateValidator.Should().BeSameAs(_qualityGateValidator.Object);
    }

    [Fact]
    public void ForAgent_SetsPrePopulatedIssueData()
    {
        var issue = new IssueDetail { Identifier = "42", Title = "Test", Description = "Desc", Labels = [] };
        var parsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = ["ac1"] };
        var comments = new List<IssueComment>
        {
            new() { Author = "user", Body = "comment", CreatedAt = DateTime.UtcNow, Id = "c-1" }
        };

        var ctx = BuildAgentContext(issue: issue, parsedIssue: parsedIssue, issueComments: comments);

        ctx.Issue.Should().BeSameAs(issue);
        ctx.ParsedIssue.Should().BeSameAs(parsedIssue);
        ctx.IssueComments.Should().BeSameAs(comments);
    }

    [Fact]
    public void ForAgent_SetsPreResolvedConfigs()
    {
        var reviewerConfigs = new List<ReviewerConfiguration>
        {
            new() { DisplayName = "R1", Agents = [] }
        };
        var qgConfigs = new List<QualityGateConfiguration>
        {
            new() { DisplayName = "QG1" }
        };
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories = []
        };

        var ctx = BuildAgentContext(
            preResolvedReviewerConfigs: reviewerConfigs,
            preResolvedQualityGateConfigs: qgConfigs,
            projectContext: projectContext);

        ctx.PreResolvedReviewerConfigs.Should().BeSameAs(reviewerConfigs);
        ctx.PreResolvedQualityGateConfigs.Should().BeSameAs(qgConfigs);
        ctx.ProjectContext.Should().BeSameAs(projectContext);
    }

    [Fact]
    public void ForAgent_DoesNotSetIssueProvider()
    {
        var ctx = BuildAgentContext();

        ctx.IssueProvider.Should().BeNull();
    }

    [Fact]
    public void ForAgent_WithNullOptionalProperties_SetsNull()
    {
        var ctx = BuildAgentContext(
            issue: null,
            parsedIssue: null,
            issueComments: null,
            preResolvedReviewerConfigs: null,
            preResolvedQualityGateConfigs: null,
            projectContext: null);

        ctx.Issue.Should().BeNull();
        ctx.ParsedIssue.Should().BeNull();
        ctx.IssueComments.Should().BeNull();
        ctx.PreResolvedReviewerConfigs.Should().BeNull();
        ctx.PreResolvedQualityGateConfigs.Should().BeNull();
        ctx.ProjectContext.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private PipelineStepContext BuildOrchestratorContext() =>
        PipelineStepContext.ForOrchestrator(
            run: _run,
            config: _config,
            repoProvider: _repoProvider.Object,
            agentProvider: _agentProvider.Object,
            brainProvider: _brainProvider.Object,
            pipelineProvider: _pipelineProvider.Object,
            cts: _cts,
            configStore: _configStore.Object,
            callbacks: _callbacks.Object,
            issueOps: _issueOps.Object,
            agentExecution: _agentExecution.Object,
            qualityGates: _qualityGates.Object,
            brainSync: _brainSync.Object,
            prOrchestrator: _prOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator.Object,
            issueProvider: _issueProvider.Object);

    private PipelineStepContext BuildAgentContext(
        IssueDetail? issue = null,
        ParsedIssue? parsedIssue = null,
        IReadOnlyList<IssueComment>? issueComments = null,
        IReadOnlyList<ReviewerConfiguration>? preResolvedReviewerConfigs = null,
        IReadOnlyList<QualityGateConfiguration>? preResolvedQualityGateConfigs = null,
        DecompositionProjectContext? projectContext = null) =>
        PipelineStepContext.ForAgent(
            run: _run,
            config: _config,
            repoProvider: _repoProvider.Object,
            agentProvider: _agentProvider.Object,
            brainProvider: _brainProvider.Object,
            pipelineProvider: _pipelineProvider.Object,
            cts: _cts,
            configStore: _configStore.Object,
            callbacks: _callbacks.Object,
            issueOps: _issueOps.Object,
            agentExecution: _agentExecution.Object,
            qualityGates: _qualityGates.Object,
            brainSync: _brainSync.Object,
            prOrchestrator: _prOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator.Object,
            issue: issue,
            parsedIssue: parsedIssue,
            issueComments: issueComments,
            preResolvedReviewerConfigs: preResolvedReviewerConfigs,
            preResolvedQualityGateConfigs: preResolvedQualityGateConfigs,
            projectContext: projectContext);
}
