using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Integration-style unit tests for <see cref="DecompositionAnalysisStep.ExecuteAsync"/>.
/// Uses a real filesystem workspace and mocked <see cref="IAgentProvider"/> to drive
/// <see cref="AdversarialReviewHelper.ExecuteReviewAsync"/> indirectly (the helper is static
/// and cannot be mocked directly — it is driven through the agent provider seam).
///
/// These tests verify that review and refinement token usage is accumulated on the pipeline run
/// after the adversarial review executes.
/// </summary>
public class DecompositionAnalysisStepExecutionTests : IDisposable
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly string _workspacePath;

    public DecompositionAnalysisStepExecutionTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"decomp-step-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));

        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();

        // Default callback setups
        _mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _mockCallbacks.Setup(c => c.NotifyChange());

        // WriteEpicContextAsync calls GetIssueAsync and ListCommentsAsync early in ExecuteAsync.
        // These must be mocked, otherwise the test throws before reaching the accumulation code.
        _mockIssueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "epic-42",
                Title = "Test Epic",
                Description = "Epic description",
                Labels = Array.Empty<string>()
            });
        _mockIssueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        // AdversarialReviewHelper calls GetLatestSessionIdAsync before dispatching the discriminator
        _mockAgent
            .Setup(a => a.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("generator-session-id");

        // Default: non-empty output so stall monitor doesn't classify as RestartSession
        _mockAgent
            .Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = _workspacePath,
                MaxDecompositionSubIssues = 5,
                MaxDecompositionSubIssueFiles = 10,
                DecompositionTimeout = TimeSpan.FromMinutes(5)
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _mockAgent.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _mockCallbacks.Object,
            IssueOps = _mockIssueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger
        };
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = $"decomp-test-{Guid.NewGuid():N}",
        IssueIdentifier = "epic-42",
        IssueTitle = "Epic: Decompose feature X",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.DecompositionAnalysis,
        WorkspacePath = _workspacePath
    };

    /// <summary>
    /// Helper: synchronously writes the review file and returns the given result.
    /// Used by discriminator mocks to simulate a real agent writing its findings file.
    /// </summary>
    private static AgentResult WriteReviewFileAndReturn(string reviewFilePath, string content, AgentResult result)
    {
        File.WriteAllText(reviewFilePath, content);
        return result;
    }

    // ── Token accumulation ────────────────────────────────────────────────

    /// <summary>
    /// When the adversarial review runs and returns ReviewTokenUsage (discriminator)
    /// and RefinementTokenUsage (refinement), both phases must appear in run.Metrics.PhaseBreakdown.
    /// This verifies the fix for the gap where DecompositionAnalysisStep discarded both usages.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithAdversarialReviewReturningTokenUsage_AccumulatesReviewAndRefinementPhases()
    {
        var run = CreateRun();
        var context = BuildContext(run);

        // Write the decomposition plan file — required by the plan-validation guard (≥20 chars)
        var planContent = "This is a valid decomposition plan with sufficient content for testing.";
        await File.WriteAllTextAsync(
            Path.Combine(_workspacePath, ".agent", "decomposition-plan.md"), planContent);

        // Note: We do NOT pre-write the review file here — AdversarialReviewHelper deletes any
        // existing review file before dispatching the discriminator. The discriminator mock (call 2)
        // must write it as a side effect to simulate a real agent producing review findings.

        // Three sequential agent calls:
        //   Call 1: main analysis agent (builds plan) — produces plan file, UseResume=false
        //   Call 2: discriminator/review agent (AdversarialReviewHelper, UseResume=false)
        //           — must write the review file as a side effect with [CRITICAL] to trigger refinement
        //   Call 3: refinement agent (AdversarialReviewHelper, UseResume=true)
        // TODO: [WARNING] Sequence-number dispatch is fragile. If DecompositionAnalysisStep.ExecuteAsync
        // gains additional agent calls before the review (e.g., brain context fetch, environment setup),
        // the call numbers shift and the discriminator mock never writes the review file, producing a
        // confusing failure unrelated to the behaviour under test. Consider distinguishing calls by
        // inspecting AgentRequest properties (UseResume, prompt keywords) instead of a counter.
        // See review finding: TestQualityReviewer WARNING line 130.
        var callSequence = 0;
        _mockAgent
            .Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync((AgentRequest req, CancellationToken _, Action<string>? _) =>
            {
                callSequence++;
                return callSequence switch
                {
                    1 => // Main analysis agent — plan file already written above
                        new AgentResult
                        {
                            ExitCode = 0,
                            OutputLines = ["Generated decomposition plan."],
                            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 }
                        },
                    2 => // Discriminator (review) — writes review file with CRITICAL finding to trigger refinement
                        WriteReviewFileAndReturn(
                            Path.Combine(_workspacePath, ".agent", "decomposition-review.md"),
                            "- [CRITICAL] Missing error handling in step 3.",
                            new AgentResult
                            {
                                ExitCode = 0,
                                OutputLines = ["Review complete."],
                                Usage = new TokenUsage { InputTokens = 50, OutputTokens = 50 } // TotalTokens = 100
                            }),
                    3 => // Refinement agent
                        new AgentResult
                        {
                            ExitCode = 0,
                            OutputLines = ["Refinement complete."],
                            Usage = new TokenUsage { InputTokens = 25, OutputTokens = 25 } // TotalTokens = 50
                        },
                    _ => new AgentResult { ExitCode = 1, OutputLines = [] }
                };
            });

        var step = new DecompositionAnalysisStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Step should succeed
        result.Should().Be(StepResult.Continue,
            "all three agent calls succeed and the review executes successfully");

        // decomposition_review: 50+50=100 total tokens from the discriminator call
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review",
            "review token usage must be accumulated even though DecompositionAnalysisStep previously discarded it");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(100,
            "discriminator usage is InputTokens=50 + OutputTokens=50 = 100 TotalTokens");

        // decomposition_refinement: 25+25=50 total tokens from the refinement call
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_refinement",
            "refinement token usage must be accumulated when a CRITICAL finding triggered refinement");
        run.Metrics.PhaseBreakdown["decomposition_refinement"].Tokens.Should().Be(50,
            "refinement usage is InputTokens=25 + OutputTokens=25 = 50 TotalTokens");

        // TotalTokens must include at minimum the review + refinement tokens
        run.TotalTokens.Should().BeGreaterThanOrEqualTo(150,
            "review (100) + refinement (50) = 150 tokens at minimum from adversarial phases");
    }

    /// <summary>
    /// When the adversarial review runs but no CRITICAL/WARNING findings exist,
    /// only decomposition_review is accumulated (no refinement agent runs).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithReviewNoFindings_AccumulatesOnlyReviewPhase()
    {
        var run = CreateRun();
        var context = BuildContext(run);

        await File.WriteAllTextAsync(
            Path.Combine(_workspacePath, ".agent", "decomposition-plan.md"),
            "This is a valid decomposition plan with sufficient content for testing.");

        // Review file with suggestions only — no CRITICAL or WARNING → no refinement
        // TODO: [WARNING] This pre-write is a no-op: AdversarialReviewHelper deletes the review file before
        // dispatching the discriminator, so the discriminator mock (call 2) always supplies the actual content.
        // The pre-written file never reaches the review parser. Remove this write to make the test's actual
        // data source (the mock side-effect) unambiguous and to avoid a false sense that the file matters here.
        // See review findings: Correctness SUGGESTION, TestQualityReviewer WARNING line 226.
        await File.WriteAllTextAsync(
            Path.Combine(_workspacePath, ".agent", "decomposition-review.md"),
            "- [SUGGESTION] Consider adding more detail to step 2.");

        var callSequence = 0;
        _mockAgent
            .Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callSequence++;
                return callSequence switch
                {
                    1 => new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = ["Plan generated."],
                        Usage = new TokenUsage { InputTokens = 100, OutputTokens = 80 }
                    },
                    2 => // Discriminator only — writes suggestions (no CRITICAL/WARNING), no refinement triggered
                        WriteReviewFileAndReturn(
                            Path.Combine(_workspacePath, ".agent", "decomposition-review.md"),
                            "- [SUGGESTION] Consider adding more detail to step 2.",
                            new AgentResult
                            {
                                ExitCode = 0,
                                OutputLines = ["Review complete."],
                                Usage = new TokenUsage { InputTokens = 30, OutputTokens = 20 } // TotalTokens = 50
                            }),
                    _ => new AgentResult { ExitCode = 1, OutputLines = [] }
                };
            });

        var step = new DecompositionAnalysisStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);

        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(50);

        run.Metrics.PhaseBreakdown.Should().NotContainKey("decomposition_refinement",
            "no CRITICAL/WARNING findings means no refinement agent was dispatched");
    }
}
