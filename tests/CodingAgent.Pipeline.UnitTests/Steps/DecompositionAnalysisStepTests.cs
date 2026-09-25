using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="DecompositionPromptBuilder"/> and <see cref="AgentLabels"/> decomposition labels.
/// Verifies that prompts contain all required instructions per the design document,
/// and that new epic labels are correctly defined with proper colors.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 2.1, 2.8, 3.4, 3.5, 3.8
/// </summary>
public class DecompositionAnalysisStepTests
{
    [Fact]
    public void BuildAnalysisPrompt_ContainsMaxSubIssuesCap()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(7, 12);

        prompt.Should().Contain("7");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsFileLimit()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("**12 files**");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneVerificationCriterion()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneAgentRunConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("single agent run");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOpenIssuesDeduplicationInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain(".agent/open-issues/");
        prompt.Should().Contain("overlap");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsReRunFeedbackInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("re-run");
        prompt.Should().Contain("feedback");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDependencyOrderingInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("backward");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOutputPathInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain(".agent/decomposition-plan.md");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsGateRejectionConcernsInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("agent:gate-rejection");
        prompt.Should().Contain("hard constraint");
        prompt.Should().Contain("which sub-issue handles it");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsJsonSchemaInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("title");
        prompt.Should().Contain("body");
        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("labels");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsSubIssuesOutputPath()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain(".agent/sub-issues/");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsIssueTemplateSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("Summary");
        prompt.Should().Contain("Acceptance Criteria");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxSubIssuesCap()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(10, 12);

        prompt.Should().Contain("10");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsOverlapCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("overlap");
        prompt.Should().Contain("open issues");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsSizingValidation()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("≤12 files");
        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsAcyclicDependencyCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("acyclic");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsCriticalFlagging()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("[CRITICAL]");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsDuplicateTitleCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("duplicate");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsReviewFindingsPath()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12);

        prompt.Should().Contain(".agent/decomposition-review.md");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsCriticalAndWarningInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12);

        prompt.Should().Contain("[CRITICAL]");
        prompt.Should().Contain("[WARNING]");
    }
}


/// <summary>
/// Step-level integration tests verifying that <see cref="DecompositionAnalysisStep"/>
/// propagates <see cref="PipelineConfiguration.MaxDecompositionSubIssues"/> into the
/// review and refinement prompts that are actually passed to the agent.
///
/// These tests mock <see cref="IAgentProvider"/> to capture every <see cref="AgentRequest"/>
/// dispatched during <c>ExecuteAsync</c>, then assert on the captured prompt text.
/// If <c>ExecuteAsync</c> were refactored to pass <c>null</c> or a hard-coded value instead of
/// <c>config.MaxDecompositionSubIssues</c>, the assertions below would fail, satisfying the
/// acceptance criterion: "a DecompositionAnalysisStep unit test shows MaxDecompositionSubIssues
/// reaching both prompts."
///
/// Feature: #3018 — Decomposition cap enforcement in review/refinement prompts.
/// </summary>
public class DecompositionAnalysisStepCapWiringTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly Mock<IAgentProvider> _agentProvider;
    private readonly Mock<IPipelineCallbacks> _callbacks;
    private readonly Mock<IAgentIssueOperations> _issueOps;
    private readonly Serilog.ILogger _logger;

    public DecompositionAnalysisStepCapWiringTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"decomp-cap-wiring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));

        _agentProvider = new Mock<IAgentProvider>();
        _callbacks = new Mock<IPipelineCallbacks>();
        _issueOps = new Mock<IAgentIssueOperations>();
        _logger = new Serilog.LoggerConfiguration().CreateLogger();

        // GetLatestSessionIdAsync is called by AdversarialReviewHelper before the review agent dispatch
        _agentProvider
            .Setup(a => a.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-id");

        // IssueOps stubs for WriteEpicContextAsync
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Epic",
                Description = "Epic body",
                Labels = []
            });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());

        // Callbacks: all void/task methods are no-ops
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.NotifyChange());
        _callbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>())).Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    /// <summary>
    /// Sets up the agent provider mock to:
    ///   1st call — analysis agent: return success (plan file is already written to disk).
    ///   2nd call — review agent: write a no-findings file and return success.
    ///   (No 3rd call because no CRITICAL/WARNING findings means no refinement.)
    /// Returns the list of AgentRequests captured in call order.
    /// </summary>
    private List<AgentRequest> SetupAgentMock()
    {
        var capturedRequests = new List<AgentRequest>();
        var callIndex = 0;

        _agentProvider
            .Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((request, _, _) =>
            {
                capturedRequests.Add(request);
                callIndex++;

                if (callIndex == 2)
                {
                    // Review agent call — write an empty findings file so no refinement is triggered
                    var reviewFilePath = Path.Combine(_workspacePath, AgentWorkspacePaths.DecompositionReviewFilePath);
                    File.WriteAllText(reviewFilePath, "No findings.");
                }

                return Task.FromResult(new AgentResult
                {
                    ExitCode = 0,
                    OutputLines = Array.Empty<string>()
                });
            });

        return capturedRequests;
    }

    private PipelineStepContext BuildContext(int maxSubIssues, int maxFiles = 12)
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "42",
            IssueTitle = "Test Epic",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.DecompositionAnalysis,
            WorkspacePath = _workspacePath
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp",
                MaxDecompositionSubIssues = maxSubIssues,
                MaxDecompositionSubIssueFiles = maxFiles,
                DecompositionTimeout = TimeSpan.FromMinutes(5)
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _agentProvider.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public async Task ExecuteAsync_ReviewPromptContainsMaxSubIssuesCapValue(int cap)
    {
        // Write a valid plan file so plan validation passes (≥20 chars, contains a table)
        var planPath = Path.Combine(_workspacePath, AgentWorkspacePaths.DecompositionPlanFilePath);
        File.WriteAllText(planPath, "# Plan\n\nSome plan content.\n\n| # | Title | Scope |\n|---|-------|-------|\n| 1 | Sub-issue 1 | Scope |");

        var capturedRequests = SetupAgentMock();
        var context = BuildContext(maxSubIssues: cap);
        var step = new DecompositionAnalysisStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // The 2nd ExecuteAsync call is the review agent — its prompt must contain the cap section
        // This fails if DecompositionAnalysisStep.ExecuteAsync passes null or a hard-coded value
        // instead of config.MaxDecompositionSubIssues.
        capturedRequests.Should().HaveCountGreaterThanOrEqualTo(2,
            "review agent must be dispatched");
        var reviewPrompt = capturedRequests[1].Prompt;
        reviewPrompt.Should().Contain($"Sub-Issue Cap ({cap})",
            "the review prompt must name the configured cap");
        reviewPrompt.Should().Contain(cap.ToString(),
            "the cap value from config.MaxDecompositionSubIssues must appear in the review prompt");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public async Task ExecuteAsync_RefinementPromptContainsAtMostNSubIssues(int cap)
    {
        // Write plan file and a review file with one WARNING finding so refinement is triggered,
        // allowing us to observe the refinement prompt (3rd call to ExecuteAsync).
        var planPath = Path.Combine(_workspacePath, AgentWorkspacePaths.DecompositionPlanFilePath);
        File.WriteAllText(planPath, "# Plan\n\nSome plan content.\n\n| # | Title | Scope |\n|---|-------|-------|\n| 1 | Sub-issue 1 | Scope |");

        var capturedRequests = new List<AgentRequest>();
        var callIndex = 0;

        _agentProvider
            .Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((request, _, _) =>
            {
                capturedRequests.Add(request);
                callIndex++;

                if (callIndex == 2)
                {
                    // Review agent: write a WARNING finding to trigger refinement
                    var reviewFilePath = Path.Combine(_workspacePath, AgentWorkspacePaths.DecompositionReviewFilePath);
                    File.WriteAllText(reviewFilePath, "[WARNING] Some sizing finding.");
                }

                return Task.FromResult(new AgentResult
                {
                    ExitCode = 0,
                    OutputLines = Array.Empty<string>()
                });
            });

        var context = BuildContext(maxSubIssues: cap);
        var step = new DecompositionAnalysisStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // 3rd call is the refinement agent — its prompt must include the cap constraint
        capturedRequests.Should().HaveCountGreaterThanOrEqualTo(3,
            "refinement agent must be dispatched when WARNING findings exist");
        var refinementPrompt = capturedRequests[2].Prompt;
        refinementPrompt.Should().Contain($"At most {cap} sub-issues",
            "the refinement prompt must list the cap as a constraint");
    }
}
public class AgentLabelsDecompositionTests
{
    [Fact]
    public void Epic_LabelConstant_HasCorrectValue()
    {
        AgentLabels.Epic.Should().Be("agent:epic");
    }

    [Fact]
    public void EpicReview_LabelConstant_HasCorrectValue()
    {
        AgentLabels.EpicReview.Should().Be("agent:epic-review");
    }

    [Fact]
    public void EpicApproved_LabelConstant_HasCorrectValue()
    {
        AgentLabels.EpicApproved.Should().Be("agent:epic-approved");
    }

    [Fact]
    public void Definitions_ContainsEpicLabel_WithPurpleColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.Epic && d.Color == "7057ff");
    }

    [Fact]
    public void Definitions_ContainsEpicReviewLabel_WithYellowColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.EpicReview && d.Color == "fbca04");
    }

    [Fact]
    public void Definitions_ContainsEpicApprovedLabel_WithGreenColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.EpicApproved && d.Color == "0e8a16");
    }

    [Fact]
    public void All_ContainsAllEpicLabels()
    {
        AgentLabels.All.Should().Contain(AgentLabels.Epic);
        AgentLabels.All.Should().Contain(AgentLabels.EpicReview);
        AgentLabels.All.Should().Contain(AgentLabels.EpicApproved);
    }

    [Fact]
    public void Definitions_EpicLabels_HaveDistinctColors()
    {
        var epicColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.Epic).Color;
        var reviewColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.EpicReview).Color;
        var approvedColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.EpicApproved).Color;

        // Epic (purple) should be distinct from review (yellow)
        epicColor.Should().NotBe(reviewColor);
    }
}
