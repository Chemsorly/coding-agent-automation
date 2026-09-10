using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests verifying that <see cref="DecompositionAnalysisStep"/> correctly accumulates
/// adversarial review and refinement token usage into the pipeline run's phase breakdown.
///
/// The acceptance criterion from issue #2386 requires: "Unit test: DecompositionAnalysisStep
/// with a mock AdversarialReviewHelper that returns non-null ReviewTokenUsage → run.TokenUsage
/// includes a 'decomposition_review' phase entry."
///
/// Because <see cref="AdversarialReviewHelper.ExecuteReviewAsync"/> is static and calls
/// <see cref="IAgentProvider.ExecuteAsync"/> internally, these tests control the provider mock
/// to drive the helper's return value — effectively exercising the full call chain from
/// DecompositionAnalysisStep through AdversarialReviewHelper to token accumulation.
/// </summary>
public class DecompositionAnalysisStepTokenAccumulationTests : IDisposable
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly string _workspacePath;
    private readonly string _agentDir;

    public DecompositionAnalysisStepTokenAccumulationTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"decomp-step-test-{Guid.NewGuid():N}");
        _agentDir = Path.Combine(_workspacePath, ".agent");
        Directory.CreateDirectory(_agentDir);

        // Default callback setup
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.NotifyChange());
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        // Default health status — process alive, not stalling
        _agentProvider
            .Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, IsProcessAlive = true });
        _agentProvider
            .Setup(p => p.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-abc");
        _agentProvider.Setup(p => p.ProviderType).Returns(AgentProviderType.KiroCli);

        // Issue ops — provide a minimal issue and empty comments
        _issueOps
            .Setup(o => o.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Epic",
                Description = "Epic description",
                Labels = Array.Empty<string>()
            });
        _issueOps
            .Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.DecompositionAnalysis,
        ProjectId = "decomp-test",
        ProjectName = "TestProject",
        WorkspacePath = _workspacePath
    };

    private PipelineStepContext BuildContext(PipelineRun run) =>
        new()
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                AgentTimeout = TimeSpan.FromMinutes(30),
                DecompositionTimeout = TimeSpan.FromMinutes(15),
                StallPollInterval = TimeSpan.FromSeconds(30),
                StallWarningInterval = TimeSpan.FromMinutes(2),
                MaxDecompositionSubIssues = 10,
                MaxDecompositionSubIssueFiles = 12,
                OutputLinesCapacity = 500,
                ChatHistoryCapacity = 50,
                QualityGateHistoryCapacity = 10,
                RetryErrorsCapacity = 10,
                BlacklistedPaths = [".agent"],
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
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger
        };

    /// <summary>
    /// Writes the decomposition plan file with sufficient content to pass the length check.
    /// </summary>
    private void WritePlanFile(string content = "This is a detailed decomposition plan with sufficient content for the review threshold.")
    {
        var planPath = Path.Combine(_agentDir, "decomposition-plan.md");
        File.WriteAllText(planPath, content);
    }

    // ── Core acceptance criterion test ────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion from issue #2386:
    /// DecompositionAnalysisStep with a mock AgentProvider (driving AdversarialReviewHelper)
    /// that returns non-null ReviewTokenUsage → run.TokenUsage includes a "decomposition_review" phase entry.
    ///
    /// AdversarialReviewHelper deletes the review file before dispatching the review agent,
    /// then reads it back after. The mock agent callback writes the review file so the helper
    /// sees findings (or absence of findings) when it reads it post-dispatch.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenReviewAgentReturnsTokens_RunIncludesDecompositionReviewPhase()
    {
        // Arrange — set up a sequence of ExecuteAsync calls:
        //   Call 1: analysis agent (main plan generation) — returns success with tokens
        //   Call 2: adversarial review agent (discriminator) — writes review file, returns success with tokens
        var reviewFilePath = Path.Combine(_agentDir, "decomposition-review.md");
        var analysisResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Generated decomposition plan"],
            Usage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 }
        };
        var reviewResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Review complete"],
            Usage = new TokenUsage { InputTokens = 600, OutputTokens = 300 }  // This is ReviewTokenUsage
        };

        var callIndex = 0;
        // TODO: [WARNING] callIndex coupling is fragile: if DecompositionAnalysisStep ever inserts
        // a pre-analysis agent call (e.g. codebase discovery) before the plan-generation call, the
        // index assumptions here silently misalign. If this test starts failing with unexpected token
        // counts, verify the call sequence in DecompositionAnalysisStep.ExecuteAsync first.
        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                var index = callIndex++;
                if (index == 1)
                {
                    // Simulate review agent writing findings file with no CRITICAL/WARNING
                    File.WriteAllText(reviewFilePath, "# Review\n\n[SUGGESTION] Minor improvement.");
                }
                return index == 0 ? analysisResult : reviewResult;
            });

        WritePlanFile();

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — step must complete successfully
        // TODO: [WARNING] If this assertion fails (StepResult.Stop) the PhaseBreakdown assertions
        // below are never evaluated, giving a misleading failure message. Diagnosis: check whether
        // _issueOps.GetIssueAsync or plan-file length caused an early return before token accumulation.
        result.Should().Be(StepResult.Continue, "adversarial review succeeded with no critical findings");

        // Assert — the decomposition_review phase entry must be present in the run
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review",
            because: "DecompositionAnalysisStep must call run.AccumulateTokenUsage(reviewResult.ReviewTokenUsage, phase: \"decomposition_review\") after ExecuteReviewAsync");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(900,
            because: "ReviewTokenUsage has InputTokens=600 + OutputTokens=300 = 900");
    }

    // ── Refinement token accumulation ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenRefinementTriggered_RunIncludesDecompositionRefinementPhase()
    {
        // Arrange — review agent writes CRITICAL findings, triggering a refinement call:
        //   Call 1: analysis agent
        //   Call 2: review agent — writes review file with [CRITICAL], returns tokens
        //   Call 3: refinement agent (UseResume=true) — returns refinement tokens
        var reviewFilePath = Path.Combine(_agentDir, "decomposition-review.md");
        var analysisResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Plan generated"],
            Usage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 }
        };
        var reviewResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Review complete — found critical issue"],
            Usage = new TokenUsage { InputTokens = 600, OutputTokens = 300 }
        };
        var refinementResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Refinement complete"],
            Usage = new TokenUsage { InputTokens = 800, OutputTokens = 400 }  // This is RefinementTokenUsage
        };

        var callIndex = 0;
        // TODO: [WARNING] callIndex coupling is fragile — see note on first integration test.
        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                var index = callIndex++;
                if (index == 1)
                {
                    // Simulate review agent writing findings with a CRITICAL finding → triggers refinement
                    File.WriteAllText(reviewFilePath, "# Review\n\n[CRITICAL] Missing acceptance criteria in sub-issue 1.");
                }
                return index switch { 0 => analysisResult, 1 => reviewResult, _ => refinementResult };
            });

        WritePlanFile();

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);

        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review",
            because: "review token usage must be accumulated regardless of whether refinement fires");
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_refinement",
            because: "DecompositionAnalysisStep must accumulate RefinementTokenUsage when non-null");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(900);
        run.Metrics.PhaseBreakdown["decomposition_refinement"].Tokens.Should().Be(1200,
            because: "RefinementTokenUsage has InputTokens=800 + OutputTokens=400 = 1200");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReviewHasNoRefinement_NoDecompositionRefinementPhase()
    {
        // Arrange — review agent writes findings with no CRITICAL/WARNING → no refinement dispatched
        var reviewFilePath = Path.Combine(_agentDir, "decomposition-review.md");
        var analysisResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Plan generated"],
            Usage = new TokenUsage { InputTokens = 1000, OutputTokens = 500 }
        };
        var reviewResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Review complete — no issues"],
            Usage = new TokenUsage { InputTokens = 400, OutputTokens = 200 }
        };

        var callIndex = 0;
        // TODO: [WARNING] callIndex coupling is fragile — see note on first integration test.
        _agentProvider
            .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                var index = callIndex++;
                if (index == 1)
                {
                    // No CRITICAL or WARNING findings
                    File.WriteAllText(reviewFilePath, "# Review\n\n[SUGGESTION] Minor improvement only.");
                }
                return index == 0 ? analysisResult : reviewResult;
            });

        WritePlanFile();

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new DecompositionAnalysisStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — review present, refinement absent
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review");
        run.Metrics.PhaseBreakdown.Should().NotContainKey("decomposition_refinement",
            because: "refinement was not triggered, so RefinementTokenUsage is null and must not be accumulated");
    }

    // ── Extension method isolation tests (remain for regression coverage) ─────

    [Fact]
    public void AccumulateTokenUsage_TokenUsageOverload_WithNonNullUsage_AddsPhaseEntry()
    {
        var run = MakeBaseRun();
        var reviewTokenUsage = new TokenUsage { InputTokens = 600, OutputTokens = 300, ReasoningTokens = 50 };

        run.AccumulateTokenUsage(reviewTokenUsage, phase: "decomposition_review");

        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(950,
            because: "TotalTokens = InputTokens(600) + OutputTokens(300) + ReasoningTokens(50)");
        run.Metrics.PhaseBreakdown["decomposition_review"].Cost.Should().BeNull(
            because: "TokenUsage overload does not carry cost");
    }

    [Fact]
    public void AccumulateTokenUsage_TokenUsageOverload_WithNullUsage_IsNoOp()
    {
        var run = MakeBaseRun();

        run.AccumulateTokenUsage((TokenUsage?)null, phase: "decomposition_review");

        run.TotalTokens.Should().Be(0);
        run.Metrics.PhaseBreakdown.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateTokenUsage_BothReviewAndRefinement_AppearSeparately()
    {
        var run = MakeBaseRun();
        var reviewUsage = new TokenUsage { InputTokens = 600, OutputTokens = 300 };
        var refinementUsage = new TokenUsage { InputTokens = 800, OutputTokens = 400 };

        run.AccumulateTokenUsage(reviewUsage, phase: "decomposition_review");
        run.AccumulateTokenUsage(refinementUsage, phase: "decomposition_refinement");

        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_review");
        run.Metrics.PhaseBreakdown.Should().ContainKey("decomposition_refinement");
        run.Metrics.PhaseBreakdown["decomposition_review"].Tokens.Should().Be(900);
        run.Metrics.PhaseBreakdown["decomposition_refinement"].Tokens.Should().Be(1200);
    }

    [Fact]
    public void AccumulateTokenUsage_TokenUsageOverload_AccumulatesTotalTokens()
    {
        var run = MakeBaseRun();
        var usage = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadTokens = 500,
            CacheWriteTokens = 200
        };

        run.AccumulateTokenUsage(usage, phase: "decomposition_review");

        run.TotalTokens.Should().Be(150, because: "TotalTokens = InputTokens(100) + OutputTokens(50)");
        run.CacheReadTokens.Should().Be(500);
        run.CacheWriteTokens.Should().Be(200);
    }

    private static PipelineRun MakeBaseRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.DecompositionAnalysis,
        ProjectId = "decomp-test",
        ProjectName = "TestProject"
    };
}
