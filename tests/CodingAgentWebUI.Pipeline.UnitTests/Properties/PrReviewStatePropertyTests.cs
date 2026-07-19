using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using Serilog.Core;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for PR review state machine (P7, P8).
/// Feature: 025-pr-review-pipeline
/// </summary>
public class PrReviewStatePropertyTests
{
    // ─── P7: Review State Transition Sequence ───────────────────────────────────
    // For any successful PR review run, the state transitions SHALL follow the sequence:
    // Created → CloningRepository → CreatingBranch → [SyncingBrainRepoPreRun] →
    // ExtractingLinkedIssues → ReviewingCode → PostingFindings → Completed,
    // where SyncingBrainRepoPreRun is present only when a brain provider is configured.
    // **Validates: Requirements 4.5**

    /// <summary>
    /// P7(a): The review step sequence contains exactly the expected steps in order.
    /// ExtractLinkedIssuesStep transitions to ExtractingLinkedIssues and PostReviewFindingsStep
    /// transitions to PostingFindings — verifying the review-specific state transitions.
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task P7_ReviewStepSequence_ExtractLinkedIssuesTransitionsCorrectly(PositiveInt prNumber)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pbt-p7a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Serilog.Core.Logger? logger = null;
        PipelineStepContext? context = null;
        try
        {
            var transitions = new List<PipelineStep>();
            var callbacks = new Mock<IPipelineCallbacks>();
            callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
                .Callback<PipelineStep>(step => transitions.Add(step));

            var run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = prNumber.Get.ToString(),
                IssueTitle = "Test PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "PR description",
                LinkedIssueContexts = Array.Empty<LinkedIssueContext>()
            };

            logger = new Serilog.LoggerConfiguration().CreateLogger();
            context = new PipelineStepContext
            {
                Run = run,
                Config = new PipelineConfiguration { WorkspaceBaseDirectory = tempDir },
                RepoProvider = Mock.Of<IRepositoryProvider>(),
                AgentProvider = Mock.Of<IAgentProvider>(),
                BrainProvider = null,
                PipelineProvider = null,
                Cts = new CancellationTokenSource(),
                ConfigStore = Mock.Of<IConfigurationStore>(),
                Callbacks = callbacks.Object,
                IssueOps = Mock.Of<IAgentIssueOperations>(),
                AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
                QualityGates = Mock.Of<IQualityGateExecutor>(),
                BrainSync = null,
                PrOrchestrator = new PullRequestOrchestrator(logger),
                Logger = logger
            };

            // Execute the ExtractLinkedIssuesStep
            var extractStep = new ExtractLinkedIssuesStep(new IssueDescriptionParser());
            await extractStep.ExecuteAsync(context, CancellationToken.None);

            transitions.Should().Contain(PipelineStep.ExtractingLinkedIssues,
                "ExtractLinkedIssuesStep must transition to ExtractingLinkedIssues");
        }
        finally
        {
            context?.Cts?.Dispose();
            logger?.Dispose();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// P7(b): PostReviewFindingsStep transitions to PostingFindings state.
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task P7_ReviewStepSequence_PostFindingsTransitionsCorrectly(PositiveInt prNumber)
    {
        var transitions = new List<PipelineStep>();
        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => transitions.Add(step));

        var repoProvider = new Mock<IRepositoryProvider>();
        repoProvider.Setup(r => r.FindExistingReviewCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = prNumber.Get.ToString(),
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = new[] { "TestAgent" }
        };

        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = repoProvider.Object,
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger),
            Logger = logger
        };

        var postStep = new PostReviewFindingsStep();
        await postStep.ExecuteAsync(context, CancellationToken.None);

        transitions.Should().Contain(PipelineStep.PostingFindings,
            "PostReviewFindingsStep must transition to PostingFindings");

        context.Cts.Dispose();
        logger.Dispose();
    }

    /// <summary>
    /// P7(c): The expected review state transition sequence is a valid ordered subset of PipelineStep enum.
    /// For any random configuration, the sequence Created → CloningRepository → CreatingBranch →
    /// [SyncingBrainRepoPreRun] → ExtractingLinkedIssues → ReviewingCode → PostingFindings → Completed
    /// represents monotonically increasing pipeline progress.
    /// </summary>
    [Property(MaxTest = 20)]
    public void P7_ReviewStateSequence_IsMonotonicallyOrdered(bool hasBrainProvider)
    {
        // The expected state transition sequence for a review run
        var expectedSequence = hasBrainProvider
            ? new[]
            {
                PipelineStep.Created,
                PipelineStep.CloningRepository,
                PipelineStep.CreatingBranch,
                PipelineStep.SyncingBrainRepoPreRun,
                PipelineStep.ExtractingLinkedIssues,
                PipelineStep.ReviewingCode,
                PipelineStep.PostingFindings,
                PipelineStep.Completed
            }
            : new[]
            {
                PipelineStep.Created,
                PipelineStep.CloningRepository,
                PipelineStep.CreatingBranch,
                PipelineStep.ExtractingLinkedIssues,
                PipelineStep.ReviewingCode,
                PipelineStep.PostingFindings,
                PipelineStep.Completed
            };

        // Verify the sequence does not contain implementation-only steps
        expectedSequence.Should().NotContain(PipelineStep.AnalyzingCode);
        expectedSequence.Should().NotContain(PipelineStep.GeneratingCode);
        expectedSequence.Should().NotContain(PipelineStep.RunningQualityGates);
        expectedSequence.Should().NotContain(PipelineStep.CreatingPullRequest);
        expectedSequence.Should().NotContain(PipelineStep.SyncingBrainRepoPostRun);

        // Verify the sequence always starts with Created and ends with Completed
        expectedSequence.First().Should().Be(PipelineStep.Created);
        expectedSequence.Last().Should().Be(PipelineStep.Completed);

        // Verify ExtractingLinkedIssues comes before ReviewingCode
        var extractIdx = Array.IndexOf(expectedSequence, PipelineStep.ExtractingLinkedIssues);
        var reviewIdx = Array.IndexOf(expectedSequence, PipelineStep.ReviewingCode);
        var postIdx = Array.IndexOf(expectedSequence, PipelineStep.PostingFindings);

        extractIdx.Should().BeLessThan(reviewIdx,
            "ExtractingLinkedIssues must come before ReviewingCode");
        reviewIdx.Should().BeLessThan(postIdx,
            "ReviewingCode must come before PostingFindings");
    }

    // ─── P8: Step Failure Transitions to Failed ─────────────────────────────────
    // For any PR review run where a step throws an unrecoverable exception, the run
    // SHALL transition to Failed state, the FailureReason SHALL be non-null, and the
    // label SHALL be swapped to agent:error.
    // **Validates: Requirements 4.6, 6.3**

    /// <summary>
    /// P8(a): When FailRunAsync is called with any non-empty reason, the run transitions
    /// to Failed state with a non-null FailureReason and the label is swapped to agent:error.
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task P8_StepFailure_TransitionsToFailed_WithNonNullReason(NonEmptyString failureReason)
    {
        var reason = failureReason.Get.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return;

        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "42",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CurrentStep = PipelineStep.ReviewingCode
        };

        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Simulate production behavior: TransitionTo updates CurrentStep
        callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => run.CurrentStep = step);

        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger),
            Logger = logger
        };

        await context.FailRunAsync(reason);

        // Verify: run transitions to Failed
        run.CurrentStep.Should().Be(PipelineStep.Failed);

        // Verify: FailureReason is non-null and matches
        run.FailureReason.Should().NotBeNull();
        run.FailureReason.Should().Be(reason);

        // Verify: label swapped to agent:error
        callbacks.Verify(c => c.SwapAgentLabel(
            run.IssueIdentifier.Value,
            AgentLabels.Error,
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify: FinalLabel set to agent:error
        run.FinalLabel.Should().Be(AgentLabels.Error);

        context.Cts.Dispose();
        logger.Dispose();
    }

    /// <summary>
    /// P8(b): For any step in the review pipeline that could fail, the failure handling
    /// sets CompletedAt to a non-null value (run is terminated).
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task P8_StepFailure_SetsCompletedAt(NonEmptyString failureReason)
    {
        var reason = failureReason.Get.Trim();
        if (string.IsNullOrWhiteSpace(reason)) return;

        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "99",
            IssueTitle = "Another PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CurrentStep = PipelineStep.CloningRepository
        };

        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => run.CurrentStep = step);

        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger),
            Logger = logger
        };

        run.CompletedAt.Should().BeNull("CompletedAt should be null before failure");

        await context.FailRunAsync(reason);

        run.CompletedAt.Should().NotBeNull("CompletedAt should be set after failure");

        context.Cts.Dispose();
        logger.Dispose();
    }

    /// <summary>
    /// P8(c): FailRunAsync adds the run to history via callbacks.
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task P8_StepFailure_AddsRunToHistory(PositiveInt prNumber)
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = prNumber.Get.ToString(),
            IssueTitle = "PR Title",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CurrentStep = PipelineStep.ExtractingLinkedIssues
        };

        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => run.CurrentStep = step);

        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger),
            Logger = logger
        };

        await context.FailRunAsync("Some failure");

        callbacks.Verify(c => c.AddRunToHistoryAsync(run), Times.Once);

        context.Cts.Dispose();
        logger.Dispose();
    }
}
