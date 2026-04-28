using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.UnitTests.Helpers;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for the PreparingForPullRequest step's interaction with
/// the quality gate retry loop, including shared retry budget, cleanup edge cases,
/// and external CI skip-when-no-changes behavior.
/// </summary>
public class PreparingForPullRequestPropertyTests
{
    /// <summary>
    /// Property: When all quality gates pass, PreparingForPullRequest appears in the transition log.
    /// </summary>
    // TODO: [review #1] [Property] with a single bool is effectively a 2-case exhaustive test; consider [Theory] with [InlineData]
    [Property(MaxTest = 20)]
    public void PreparingForPullRequest_AppearsInTransitionLog_WhenGatesPass(bool externalCiEnabled)
    {
        var transitionLog = new List<PipelineStep>();
        var service = CreateService(
            allInitialGatesPass: true,
            allFinalGatesPass: true,
            externalCiEnabled: externalCiEnabled);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        transitionLog.Should().Contain(PipelineStep.PreparingForPullRequest);
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    /// <summary>
    /// Property: Cleanup with no changes + external CI enabled → completes without error.
    /// The pipeline skips external CI for the final QG when cleanup produces no changes.
    /// </summary>
    // TODO: [review #1] [Property] with a single bool is effectively a 2-case exhaustive test; consider [Theory] with [InlineData]
    [Property(MaxTest = 10)]
    public void CleanupNoChanges_WithExternalCi_CompletesWithoutError(bool externalCiEnabled)
    {
        var service = CreateService(
            allInitialGatesPass: true,
            allFinalGatesPass: true,
            externalCiEnabled: externalCiEnabled,
            cleanupProducesChanges: false);

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.IsDraftPr.Should().BeFalse();
    }

    /// <summary>
    /// Property: Cleanup with changes + final QG failure → enters retry loop from shared budget.
    /// The retry count after final QG failure should be exactly initialRetriesUsed + 1.
    /// </summary>
    [Property(MaxTest = 20)]
    public void CleanupWithChanges_FinalQgFail_EntersRetryLoopFromSharedBudget(PositiveInt maxRetriesRaw)
    {
        var maxRetries = (maxRetriesRaw.Get % 4) + 2; // 2-5, need at least 2 for this scenario

        var transitionLog = new List<PipelineStep>();
        // Initial QG passes, final QG fails once then passes on retry
        var qgCallCount = 0;
        var service = CreateServiceWithCustomGates(maxRetries, () =>
        {
            qgCallCount++;
            // Call 1: initial QG pass. Call 2: final QG fail. Call 3+: pass.
            return qgCallCount != 2;
        });

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.RetryCount.Should().Be(1);
        // Verify the retry loop was entered after PreparingForPullRequest
        // TODO: [review #5] ContainInOrder doesn't verify PreparingForPullRequest appears exactly once; consider adding a count assertion
        transitionLog.Should().ContainInOrder(
            PipelineStep.PreparingForPullRequest,
            PipelineStep.RunningQualityGates,
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest);
    }

    /// <summary>
    /// Property: Shared retry budget is not reset after cleanup step.
    /// If initial QG uses some retries, the final QG retry loop continues from that count.
    /// </summary>
    // TODO: [review #6] maxRetries = initialRetriesUsed + 2 always leaves 1 unused retry; consider testing boundary where budget is exactly exhausted
    [Property(MaxTest = 20)]
    public void SharedRetryBudget_NotResetAfterCleanup(PositiveInt initialRetriesRaw)
    {
        var initialRetriesUsed = (initialRetriesRaw.Get % 3) + 1; // 1-3
        var maxRetries = initialRetriesUsed + 2; // Enough budget for initial retries + final QG retry

        var qgCallCount = 0;
        // Initial QG: fail for initialRetriesUsed attempts, then pass.
        // Final QG after cleanup: fail once, then pass.
        var service = CreateServiceWithCustomGates(maxRetries, () =>
        {
            qgCallCount++;
            // Calls 1..initialRetriesUsed: fail (initial QG retries)
            // Call initialRetriesUsed+1: pass (initial QG passes, enters cleanup)
            // Call initialRetriesUsed+2: fail (final QG after cleanup)
            // Call initialRetriesUsed+3: pass (final QG retry passes)
            if (qgCallCount <= initialRetriesUsed) return false;
            if (qgCallCount == initialRetriesUsed + 1) return true;
            if (qgCallCount == initialRetriesUsed + 2) return false;
            return true;
        });

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        // Total retries = initialRetriesUsed (from initial loop) + 1 (from final QG loop)
        run.RetryCount.Should().Be(initialRetriesUsed + 1);
    }

    /// <summary>
    /// Property: Retries exhausted after cleanup → draft PR created.
    /// When initial QG uses all retries minus one, and final QG fails, the last retry is used
    /// and then a draft PR is created.
    /// </summary>
    // TODO: [review #3] Draft PR assertion depends on CreatePullRequestAsync mock in CreateBaseMocks; missing mock would cause confusing NullReferenceException
    [Property(MaxTest = 20)]
    public void RetriesExhaustedAfterCleanup_CreatesDraftPr(PositiveInt maxRetriesRaw)
    {
        var maxRetries = (maxRetriesRaw.Get % 4) + 1; // 1-4

        var qgCallCount = 0;
        // Initial QG: pass on first call. Final QG after cleanup: always fail.
        var service = CreateServiceWithCustomGates(maxRetries, () =>
        {
            qgCallCount++;
            return qgCallCount == 1; // Only first call passes
        });

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.IsDraftPr.Should().BeTrue();
        run.RetryCount.Should().Be(maxRetries);
    }

    /// <summary>
    /// Property: For any combination of initial retries used and final QG pass/fail sequence,
    /// the total retry count never exceeds MaxRetries and the terminal state is correct.
    /// </summary>
    [Property(MaxTest = 50)]
    public void TotalRetryCount_NeverExceedsMaxRetries(
        PositiveInt maxRetriesRaw,
        PositiveInt initialFailsRaw,
        NonNegativeInt finalFailsRaw)
    {
        var maxRetries = (maxRetriesRaw.Get % 5) + 1; // 1-5
        var initialFails = initialFailsRaw.Get % maxRetries; // 0..maxRetries-1 (must be < maxRetries so initial loop passes and enters cleanup)
        var finalFails = finalFailsRaw.Get % (maxRetries + 2); // 0..maxRetries+1

        var qgCallCount = 0;
        var service = CreateServiceWithCustomGates(maxRetries, () =>
        {
            qgCallCount++;
            // Initial QG loop: fail for 'initialFails' calls, then pass
            if (qgCallCount <= initialFails) return false;
            if (qgCallCount == initialFails + 1) return true; // Pass → enters cleanup
            // Final QG loop: fail for 'finalFails' calls, then pass
            var finalCallIndex = qgCallCount - (initialFails + 1);
            return finalCallIndex > finalFails;
        });

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Core property: total retries never exceed MaxRetries
        run.RetryCount.Should().BeLessThanOrEqualTo(maxRetries);

        // Terminal state is either Completed or Failed
        run.CurrentStep.Should().BeOneOf(PipelineStep.Completed, PipelineStep.Failed);

        if (run.CurrentStep == PipelineStep.Failed)
        {
            run.IsDraftPr.Should().BeTrue();
            run.RetryCount.Should().Be(maxRetries);
        }
    }

    // --- Factory methods ---

    /// <summary>
    /// Creates a service with simple pass/fail configuration for initial and final quality gates.
    /// </summary>
    private static PipelineOrchestrationService CreateService(
        bool allInitialGatesPass,
        bool allFinalGatesPass,
        bool externalCiEnabled = false,
        bool cleanupProducesChanges = true)
    {
        var (configStore, factory, _, repoProvider, _, logger) = CreateBaseMocks();

        var config = TestPipelineConfig.Default() with { ExternalCiEnabled = externalCiEnabled };
        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        if (externalCiEnabled)
        {
            configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProviderConfig>
                {
                    new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI" }
                });

            var mockPipelineProvider = new Mock<IPipelineProvider>();
            mockPipelineProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineRunStatus
                {
                    State = PipelineRunState.Passed,
                    Jobs = new[] { new PipelineJobResult { Name = "build", State = PipelineRunState.Passed } }
                });
            factory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>())).Returns(mockPipelineProvider.Object);
        }

        if (!cleanupProducesChanges)
        {
            // After initial commit succeeds, subsequent commits throw "No changes"
            // TODO: [review #8] Mock throws on all calls after the first; would break if a retry triggers a third commit attempt (safe for current test scenarios)
            var commitCallCount = 0;
            repoProvider.Setup(p => p.CommitAllAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
                {
                    commitCallCount++;
                    if (commitCallCount > 1)
                        throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");
                    return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
                });

            repoProvider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("abc123");
        }

        // TODO: [.NET review #1] Use Interlocked.Increment for qgCallCount to match project convention (PipelineStateTransitionPropertyTests.CreateServiceWithRandomGateResults)
        var qgCallCount = 0;
        var mockValidator = new Mock<IQualityGateValidator>();
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                qgCallCount++;
                // Call 1 = initial QG, Call 2 = final QG after cleanup
                var passed = qgCallCount == 1 ? allInitialGatesPass : allFinalGatesPass;
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = passed, Details = passed ? "OK" : "Failed" },
                    Tests = new GateResult { GateName = "Tests", Passed = passed, Details = passed ? "OK" : "Failed" }
                };
            });

        return new PipelineOrchestrationService(
            configStore.Object, factory.Object,
            new IssueDescriptionParser(), mockValidator.Object,
            new CiLogWriter(logger.Object), logger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }

    /// <summary>
    /// Creates a service with a custom gate result function for fine-grained control
    /// over which QG calls pass or fail.
    /// </summary>
    // TODO: [review #2] PipelineConfiguration uses production defaults for StallPollInterval (30s); if tests become slow, set explicitly
    private static PipelineOrchestrationService CreateServiceWithCustomGates(
        int maxRetries, Func<bool> gatePassFunc)
    {
        var (configStore, factory, _, _, _, logger) = CreateBaseMocks();

        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = false }
            });

        var mockValidator = new Mock<IQualityGateValidator>();
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var passed = gatePassFunc();
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = passed, Details = passed ? "OK" : "Build failed" },
                    Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
                };
            });

        return new PipelineOrchestrationService(
            configStore.Object, factory.Object,
            new IssueDescriptionParser(), mockValidator.Object,
            new CiLogWriter(logger.Object), logger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }

    /// <summary>
    /// Creates the common mock setup shared by all test service factories.
    /// </summary>
    private static (Mock<IConfigurationStore> ConfigStore, Mock<IProviderFactory> Factory,
        Mock<IIssueProvider> IssueProvider, Mock<IRepositoryProvider> RepoProvider,
        Mock<IAgentProvider> AgentProvider, Mock<Serilog.ILogger> Logger) CreateBaseMocks()
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "issue-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "agent-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test" }
            });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42", Title = "Test Issue", Description = "Test description",
                Labels = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        mockIssueProvider.Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    var dir = Path.Combine(req.WorkspacePath, ".kiro");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "analysis.md"), new string('x', 200));
                    var assessment = new { recommendation = "ready", reason = "Test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
                    File.WriteAllText(Path.Combine(dir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(assessment, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });
        mockAgentProvider.Setup(p => p.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockAgentProvider.Setup(p => p.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockIssueProvider.Object);
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>())).Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>())).Returns(mockAgentProvider.Object);

        var mockLogger = new Mock<Serilog.ILogger>();

        return (mockConfigStore, mockFactory, mockIssueProvider, mockRepoProvider, mockAgentProvider, mockLogger);
    }
}
