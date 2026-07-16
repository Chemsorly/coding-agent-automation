using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Characterization tests for PipelineRun: step transitions, ToSummary() field mapping,
/// status lifecycle, timing invariants, and PipelineRunSummary serialization round-trip.
/// </summary>
public class PipelineRunTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    #region IsTerminal — Terminal Step Identification

    [Theory]
    [InlineData(PipelineStep.Completed)]
    [InlineData(PipelineStep.Failed)]
    [InlineData(PipelineStep.Cancelled)]
    public void IsTerminal_TerminalSteps_ReturnsTrue(PipelineStep step)
    {
        step.IsTerminal().Should().BeTrue();
    }

    [Theory]
    [InlineData(PipelineStep.Created)]
    [InlineData(PipelineStep.CloningRepository)]
    [InlineData(PipelineStep.SyncingBrainRepoPreRun)]
    [InlineData(PipelineStep.AnalyzingCode)]
    [InlineData(PipelineStep.GeneratingCode)]
    [InlineData(PipelineStep.RunningQualityGates)]
    [InlineData(PipelineStep.CreatingPullRequest)]
    [InlineData(PipelineStep.RunningEnvironmentSetup)]
    public void IsTerminal_NonTerminalSteps_ReturnsFalse(PipelineStep step)
    {
        step.IsTerminal().Should().BeFalse();
    }

    #endregion

    #region Step Transitions — CurrentStep volatile property

    // TODO: This test is tautological — it sets a property and reads it back on a single thread.
    // Since CurrentStep has no validation logic, this will always pass regardless of implementation.
    // Consider adding a concurrent read/write stress test to validate volatile guarantees,
    // or an invalid-transition test (e.g., Completed→CloningRepository) to document that no guard exists.
    [Fact]
    public void CurrentStep_VolatileReadWrite_MaintainsConsistency()
    {
        var run = PipelineRun.Create(
            runId: "run-1",
            issueIdentifier: "org/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        // Write various steps and verify each is readable immediately after
        run.CurrentStep = PipelineStep.CloningRepository;
        run.CurrentStep.Should().Be(PipelineStep.CloningRepository);

        run.CurrentStep = PipelineStep.GeneratingCode;
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);

        run.CurrentStep = PipelineStep.Completed;
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void CurrentStep_DefaultsToCreated_WithoutExplicitInit()
    {
        // Raw object-initializer construction (not the factory)
        // verifies the int backing field defaults to 0 == PipelineStep.Created
        #pragma warning disable CS0618
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow
        };
        #pragma warning restore CS0618

        run.CurrentStep.Should().Be(PipelineStep.Created);
    }

    #endregion

    #region ToSummary — Field Mapping Completeness

    [Fact]
    public void ToSummary_MapsAllFieldsCorrectly()
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 6, 1, 10, 15, 0, TimeSpan.Zero);

        var run = PipelineRun.Create(
            runId: "run-completeness",
            issueIdentifier: "org/repo#42",
            issueTitle: "Fix critical bug",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Review,
            startedAt: startedAt,
            initiatedBy: "loop",
            agentId: "agent-dotnet-1",
            agentProviderConfigId: "ap-1",
            brainProviderConfigId: "bp-1",
            reviewPrBranchName: "feature/fix",
            reviewPrTargetBranch: "main",
            reviewPrUrl: "https://github.com/org/repo/pull/42",
            reviewPrDescription: "Fix the bug",
            reviewPrAuthor: "dev-user",
            decompositionSource: "project-level");

        // Set mutable properties
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted(completedAt);
        run.PullRequestUrl = "https://github.com/org/repo/pull/99";
        run.ModelName = "claude-sonnet-4.6";
        run.BrainUpdatesPushed = true;
        run.AnalysisRecommendation = AnalysisGateResult.Ready;
        run.FailureReason = null; // explicit null for completed run
        run.RetryCount = 2;
        run.TotalTokens = 150_000;
        run.TotalCost = 3.50m;
        run.DecompositionSubIssuesCreated = 5;
        run.DecompositionSubIssuesAttempted = 6;
        run.ProjectName = "MyProject";
        run.CodeReviewAgentsRun = ["Correctness", "Security"];
        run.SetCodeReviewCounts(critical: 1, warning: 3, suggestion: 7);
        run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 42,
            BranchName = "feature/fix",
            Url = "https://github.com/org/repo/pull/42",
            IsDraft = false
        };
        run.Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = new DateTime(2026, 6, 1, 10, 15, 0, DateTimeKind.Utc),
            Harness = new HarnessFeedback
            {
                Category = "none",
                MissingContext = [],
                MissingCapabilities = [],
                PromptIssues = [],
                Suggestions = []
            },
            Issue = null
        };
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(10_000, 0.25m);
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(140_000, 3.25m);

        // Act
        var summary = run.ToSummary();

        // Assert all 32 fields
        summary.RunId.Should().Be("run-completeness");
        summary.IssueIdentifier.Should().Be("org/repo#42");
        summary.IssueTitle.Should().Be("Fix critical bug");
        summary.FinalStep.Should().Be(PipelineStep.Completed);
        #pragma warning disable CS0618
        summary.StartedAt.Should().Be(startedAt.UtcDateTime);
        summary.CompletedAt.Should().Be(completedAt.UtcDateTime);
        #pragma warning restore CS0618
        summary.StartedAtOffset.Should().Be(startedAt);
        summary.CompletedAtOffset.Should().Be(completedAt);
        summary.RetryCount.Should().Be(2);
        summary.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/99");
        summary.RunType.Should().Be(PipelineRunType.Review);
        summary.ReviewPrUrl.Should().Be("https://github.com/org/repo/pull/42");
        summary.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Correctness", "Security" });
        summary.CodeReviewCriticalCount.Should().Be(1);
        summary.CodeReviewWarningCount.Should().Be(3);
        summary.CodeReviewSuggestionCount.Should().Be(7);
        summary.ModelName.Should().Be("claude-sonnet-4.6");
        summary.BrainRepoUsed.Should().BeTrue();
        summary.BrainUpdatesPushed.Should().BeTrue();
        summary.AgentId.Should().Be("agent-dotnet-1");
        summary.InitiatedBy.Should().Be("loop");
        summary.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        summary.IsRework.Should().BeTrue();
        summary.FailureReason.Should().BeNull();
        summary.Feedback.Should().BeSameAs(run.Feedback);
        summary.TotalTokens.Should().Be(150_000);
        summary.TotalCost.Should().Be(3.50m);
        summary.PhaseBreakdown.Should().NotBeNull();
        summary.PhaseBreakdown!["analysis"].Tokens.Should().Be(10_000);
        summary.PhaseBreakdown["analysis"].Cost.Should().Be(0.25m);
        summary.PhaseBreakdown["codegen"].Tokens.Should().Be(140_000);
        summary.PhaseBreakdown["codegen"].Cost.Should().Be(3.25m);
        summary.DecompositionSubIssuesCreated.Should().Be(5);
        summary.DecompositionSubIssuesAttempted.Should().Be(6);
        summary.ProjectName.Should().Be("MyProject");
        summary.DecompositionSource.Should().Be("project-level");
    }

    [Fact]
    public void ToSummary_NonTerminalCurrentStep_FinalStepReflectsCurrentState()
    {
        // Documents ARC-10 edge case: FinalStep = CurrentStep without terminal state guard
        var run = PipelineRun.Create(
            runId: "run-arc10",
            issueIdentifier: "org/repo#7",
            issueTitle: "In-progress run",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        run.CurrentStep = PipelineStep.RunningQualityGates;

        var summary = run.ToSummary();

        // Current behavior: FinalStep reflects non-terminal CurrentStep
        summary.FinalStep.Should().Be(PipelineStep.RunningQualityGates);
        summary.FinalStep.IsTerminal().Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToSummary_BrainRepoUsed_DerivedFromBrainProviderConfigId(bool hasBrainProvider)
    {
        var run = PipelineRun.Create(
            runId: "run-brain",
            issueIdentifier: "org/repo#1",
            issueTitle: "Brain test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            brainProviderConfigId: hasBrainProvider ? "bp-1" : null);

        var summary = run.ToSummary();

        summary.BrainRepoUsed.Should().Be(hasBrainProvider);
    }

    #endregion

    #region Status Lifecycle — Happy Path

    // TODO: Only the happy path (Created→…→Completed) is covered. The issue requires
    // Created→Running→Failed and Created→Running→Cancelled lifecycle tests. Add tests
    // that set FailureReason and transition to Failed/Cancelled to guard against regressions
    // in those flows. Also missing: invalid/backwards transition characterization test
    // (e.g., Completed→CloningRepository) to document that no state machine guard exists.
    [Fact]
    public void StatusLifecycle_HappyPath_CreatedToCompleted()
    {
        var run = PipelineRun.Create(
            runId: "run-lifecycle",
            issueIdentifier: "org/repo#10",
            issueTitle: "Lifecycle test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        // Initial state
        run.CurrentStep.Should().Be(PipelineStep.Created);
        run.CompletedAtOffset.Should().BeNull();

        // Simulate progression through intermediate steps
        run.CurrentStep = PipelineStep.CloningRepository;
        run.CurrentStep.Should().Be(PipelineStep.CloningRepository);

        run.CurrentStep = PipelineStep.AnalyzingCode;
        run.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);

        run.CurrentStep = PipelineStep.GeneratingCode;
        run.CurrentStep.Should().Be(PipelineStep.GeneratingCode);

        run.CurrentStep = PipelineStep.RunningQualityGates;
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates);

        // Mark completed and transition to terminal state
        run.MarkCompleted();
        run.CurrentStep = PipelineStep.Completed;

        // Final state verification
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CurrentStep.IsTerminal().Should().BeTrue();
        run.CompletedAtOffset.Should().NotBeNull();
    }

    #endregion

    #region Timing Invariants

    // TODO: This test does not verify invariant enforcement — it supplies valid values
    // (completedAt > startedAt) so it always passes. The model's MarkCompleted() silently
    // accepts timestamps earlier than StartedAt. Add a characterization test with
    // completedAt < startedAt to document current (unguarded) behavior.
    [Fact]
    public void TimingInvariant_CompletedAtOffset_IsOnOrAfterStartedAtOffset()
    {
        // Use explicit timestamps for deterministic CI behavior
        var startedAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

        var run = PipelineRun.Create(
            runId: "run-timing",
            issueIdentifier: "org/repo#5",
            issueTitle: "Timing test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            startedAt: startedAt);

        run.MarkCompleted(completedAt);

        run.CompletedAtOffset.Should().NotBeNull();
        run.CompletedAtOffset!.Value.Should().BeOnOrAfter(run.StartedAtOffset);
    }

    // TODO: This test relies on wall-clock monotonicity between two successive UtcNow calls.
    // NTP adjustments or CI clock skew could cause a spurious failure. The deterministic-
    // timestamp test above adequately covers the invariant — consider removing this or
    // accepting a tolerance margin.
    [Fact]
    public void TimingInvariant_RealClock_CompletedAfterStarted()
    {
        // Uses real clock to document real-world behavior
        var run = PipelineRun.Create(
            runId: "run-timing-real",
            issueIdentifier: "org/repo#6",
            issueTitle: "Real timing test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        run.MarkCompleted();

        run.CompletedAtOffset.Should().NotBeNull();
        run.CompletedAtOffset!.Value.Should().BeOnOrAfter(run.StartedAtOffset);
    }

    #endregion

    #region Serialization Round-Trip — PipelineRunSummary

    [Fact]
    public void PipelineRunSummary_JsonRoundTrip_PreservesAllFields()
    {
        var startedAt = new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTimeOffset(2026, 5, 20, 8, 12, 30, TimeSpan.Zero);

        #pragma warning disable CS0618
        var original = new PipelineRunSummary
        {
            RunId = "run-serial-1",
            IssueIdentifier = "org/repo#77",
            IssueTitle = "Serialization test issue",
            FinalStep = PipelineStep.Completed,
            StartedAt = startedAt.UtcDateTime,
            CompletedAt = completedAt.UtcDateTime,
            StartedAtOffset = startedAt,
            CompletedAtOffset = completedAt,
            RetryCount = 1,
            PullRequestUrl = "https://github.com/org/repo/pull/77",
            RunType = PipelineRunType.Implementation,
            ReviewPrUrl = "https://github.com/org/repo/pull/50",
            CodeReviewAgentsRun = ["Correctness", "Security", "Performance"],
            CodeReviewCriticalCount = 2,
            CodeReviewWarningCount = 5,
            CodeReviewSuggestionCount = 10,
            ModelName = "claude-sonnet-4.6",
            BrainRepoUsed = true,
            BrainUpdatesPushed = true,
            AgentId = "agent-dotnet-1",
            InitiatedBy = "loop",
            AnalysisRecommendation = AnalysisGateResult.Ready,
            IsRework = true,
            FailureReason = null,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = new DateTime(2026, 5, 20, 8, 12, 0, DateTimeKind.Utc),
                Harness = new HarnessFeedback
                {
                    Category = "missing file context",
                    StuckReason = null,
                    MissingContext = ["tsconfig.json"],
                    MissingCapabilities = ["database access"],
                    PromptIssues = ["contradictory instructions"],
                    Suggestions = ["add retry logic"]
                },
                Issue = new IssueFeedback
                {
                    Category = "contradictory acceptance criteria",
                    Description = "AC #2 conflicts with AC #4",
                    AffectedFiles = ["src/main.ts", "src/config.ts"],
                    HumanActionNeeded = "Clarify which criterion takes priority"
                }
            },
            TotalTokens = 250_000,
            TotalCost = 5.75m,
            DecompositionSubIssuesCreated = 3,
            DecompositionSubIssuesAttempted = 4,
            ProjectName = "TestProject",
            DecompositionSource = "template-level",
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = new PhaseUsage(50_000, 1.25m),
                ["codegen"] = new PhaseUsage(200_000, 4.50m)
            }
        };
        #pragma warning restore CS0618

        // Act: serialize → deserialize
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PipelineRunSummary>(json, JsonOptions);

        // Assert all fields preserved
        deserialized.Should().NotBeNull();
        deserialized!.RunId.Should().Be(original.RunId);
        deserialized.IssueIdentifier.Should().Be(original.IssueIdentifier);
        deserialized.IssueTitle.Should().Be(original.IssueTitle);
        deserialized.FinalStep.Should().Be(original.FinalStep);
        #pragma warning disable CS0618
        deserialized.StartedAt.Should().Be(original.StartedAt);
        deserialized.CompletedAt.Should().Be(original.CompletedAt);
        #pragma warning restore CS0618
        deserialized.StartedAtOffset.Should().Be(original.StartedAtOffset);
        deserialized.CompletedAtOffset.Should().Be(original.CompletedAtOffset);
        deserialized.RetryCount.Should().Be(original.RetryCount);
        deserialized.PullRequestUrl.Should().Be(original.PullRequestUrl);
        deserialized.RunType.Should().Be(original.RunType);
        deserialized.ReviewPrUrl.Should().Be(original.ReviewPrUrl);
        deserialized.CodeReviewAgentsRun.Should().BeEquivalentTo(original.CodeReviewAgentsRun);
        deserialized.CodeReviewCriticalCount.Should().Be(original.CodeReviewCriticalCount);
        deserialized.CodeReviewWarningCount.Should().Be(original.CodeReviewWarningCount);
        deserialized.CodeReviewSuggestionCount.Should().Be(original.CodeReviewSuggestionCount);
        deserialized.ModelName.Should().Be(original.ModelName);
        deserialized.BrainRepoUsed.Should().Be(original.BrainRepoUsed);
        deserialized.BrainUpdatesPushed.Should().Be(original.BrainUpdatesPushed);
        deserialized.AgentId.Should().Be(original.AgentId);
        deserialized.InitiatedBy.Should().Be(original.InitiatedBy);
        deserialized.AnalysisRecommendation.Should().Be(original.AnalysisRecommendation);
        deserialized.IsRework.Should().Be(original.IsRework);
        deserialized.FailureReason.Should().Be(original.FailureReason);
        deserialized.TotalTokens.Should().Be(original.TotalTokens);
        deserialized.TotalCost.Should().Be(original.TotalCost);
        deserialized.DecompositionSubIssuesCreated.Should().Be(original.DecompositionSubIssuesCreated);
        deserialized.DecompositionSubIssuesAttempted.Should().Be(original.DecompositionSubIssuesAttempted);
        deserialized.ProjectName.Should().Be(original.ProjectName);
        deserialized.DecompositionSource.Should().Be(original.DecompositionSource);

        // Feedback nested object
        deserialized.Feedback.Should().NotBeNull();
        deserialized.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
        deserialized.Feedback.CollectedAtUtc.Should().Be(original.Feedback.CollectedAtUtc);
        deserialized.Feedback.Harness.Category.Should().Be("missing file context");
        deserialized.Feedback.Harness.MissingContext.Should().BeEquivalentTo(new[] { "tsconfig.json" });
        deserialized.Feedback.Harness.MissingCapabilities.Should().BeEquivalentTo(new[] { "database access" });
        deserialized.Feedback.Harness.PromptIssues.Should().BeEquivalentTo(new[] { "contradictory instructions" });
        deserialized.Feedback.Harness.Suggestions.Should().BeEquivalentTo(new[] { "add retry logic" });
        deserialized.Feedback.Issue.Should().NotBeNull();
        deserialized.Feedback.Issue!.Category.Should().Be("contradictory acceptance criteria");
        deserialized.Feedback.Issue.Description.Should().Be("AC #2 conflicts with AC #4");
        deserialized.Feedback.Issue.AffectedFiles.Should().BeEquivalentTo(new[] { "src/main.ts", "src/config.ts" });
        deserialized.Feedback.Issue.HumanActionNeeded.Should().Be("Clarify which criterion takes priority");

        // PhaseBreakdown
        deserialized.PhaseBreakdown.Should().NotBeNull();
        deserialized.PhaseBreakdown.Should().HaveCount(2);
        deserialized.PhaseBreakdown!["analysis"].Tokens.Should().Be(50_000);
        deserialized.PhaseBreakdown["analysis"].Cost.Should().Be(1.25m);
        deserialized.PhaseBreakdown["codegen"].Tokens.Should().Be(200_000);
        deserialized.PhaseBreakdown["codegen"].Cost.Should().Be(4.50m);
    }

    #endregion

    #region LabelTargetKind — RunType-based routing

    [Fact]
    public void LabelTargetKind_ReviewRun_ReturnsPullRequest()
    {
        var run = PipelineRun.Create(
            runId: "run-ltk-review",
            issueIdentifier: "org/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Review);

        run.LabelTargetKind.Should().Be(LabelTargetKind.PullRequest);
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public void LabelTargetKind_NonReviewRun_ReturnsIssue(PipelineRunType runType)
    {
        var run = PipelineRun.Create(
            runId: "run-ltk-nonreview",
            issueIdentifier: "org/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: runType);

        run.LabelTargetKind.Should().Be(LabelTargetKind.Issue);
    }

    #endregion

    // TODO: These tests verify ProviderConfigIdForLabel in isolation but no test exists to verify
    // that passing a targetKind different from run.LabelTargetKind at the call-site level (e.g.,
    // AgentIssueOperations.SwapLabelAsync) is either unsupported or produces consistent routing.
    // Consider adding a call-site-level characterization test to document this contract.
    #region ProviderConfigIdForLabel — Provider routing by RunType

    // TODO: This test mirrors the implementation 1:1 (trivial ternary). Consider adding a reverting
    // scenario or call-site-level behavioral test for stronger regression confidence.
    [Fact]
    public void ProviderConfigIdForLabel_ReviewRun_ReturnsRepoProviderConfigId()
    {
        var run = PipelineRun.Create(
            runId: "run-pcid-review",
            issueIdentifier: "org/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Review);

        run.ProviderConfigIdForLabel.Should().Be("rp-1");
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public void ProviderConfigIdForLabel_NonReviewRun_ReturnsIssueProviderConfigId(PipelineRunType runType)
    {
        var run = PipelineRun.Create(
            runId: "run-pcid-nonreview",
            issueIdentifier: "org/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: runType);

        run.ProviderConfigIdForLabel.Should().Be("ip-1");
    }

    #endregion
}
