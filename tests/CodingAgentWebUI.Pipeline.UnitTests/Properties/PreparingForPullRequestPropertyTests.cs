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
/// the quality gate retry loop, covering cleanup edge cases and shared retry budget.
/// </summary>
public class PreparingForPullRequestPropertyTests
{
    /// <summary>
    /// PreparingForPullRequest appears in the transition log when all quality gates pass.
    /// </summary>
    [Fact]
    public async Task PreparingForPullRequest_AppearsInTransitionLog_WhenQualityGatesPass()
    {
        var transitionLog = new List<PipelineStep>();
        var service = CreateService(allGatesPass: true);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        transitionLog.Should().Contain(PipelineStep.PreparingForPullRequest);
        transitionLog.Should().ContainInOrder(
            PipelineStep.RunningQualityGates,
            PipelineStep.PreparingForPullRequest,
            PipelineStep.CreatingPullRequest);
        run.CurrentStep.Should().Be(PipelineStep.Completed);
    }

    /// <summary>
    /// Cleanup makes no changes + external CI enabled → pipeline completes without error.
    /// External CI is skipped because there are no new changes to validate.
    /// </summary>
    [Fact]
    public async Task CleanupNoChanges_WithExternalCi_CompletesWithoutError()
    {
        var transitionLog = new List<PipelineStep>();
        var service = CreateService(allGatesPass: true, externalCiEnabled: true, cleanupProducesChanges: false);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        transitionLog.Should().Contain(PipelineStep.PreparingForPullRequest);
        run.IsDraftPr.Should().BeFalse();
    }

    /// <summary>
    /// Cleanup makes changes + final QG fails → enters retry loop using shared budget.
    /// With maxRetries=2, initial QG passes (0 retries used), final QG fails, retry succeeds.
    /// </summary>
    [Fact]
    public async Task CleanupWithChanges_FinalQgFailure_EntersRetryLoop()
    {
        var transitionLog = new List<PipelineStep>();
        // Initial QG passes, final QG: fail then pass
        var service = CreateServiceWithGateSequence(
            gateResults: [true, false, true],
            maxRetries: 2);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.RetryCount.Should().Be(1);
        // Should have gone through PreparingForPullRequest, then back to GeneratingCode for retry
        transitionLog.Should().Contain(PipelineStep.PreparingForPullRequest);
        var prepIdx = transitionLog.IndexOf(PipelineStep.PreparingForPullRequest);
        // After PreparingForPullRequest, should see RunningQualityGates (final), then GeneratingCode (retry)
        transitionLog.Skip(prepIdx).Should().Contain(PipelineStep.GeneratingCode);
    }

    /// <summary>
    /// Shared retry budget is not reset after cleanup step.
    /// Initial QG uses 1 retry, then cleanup, final QG fails → only remaining retries available.
    /// </summary>
    [Fact]
    public async Task SharedRetryBudget_NotResetAfterCleanup()
    {
        // maxRetries=2: initial QG fail, retry pass (1 retry used), cleanup, final QG fail, retry pass (2nd retry used)
        var service = CreateServiceWithGateSequence(
            gateResults: [false, true, false, true],
            maxRetries: 2);

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.RetryCount.Should().Be(2);
    }

    /// <summary>
    /// Retries exhausted after cleanup → draft PR created.
    /// Initial QG passes (0 retries), cleanup, final QG fails, all retries exhausted → draft PR.
    /// </summary>
    [Fact]
    public async Task RetriesExhaustedAfterCleanup_CreatesDraftPr()
    {
        // maxRetries=1: initial QG passes, final QG fails, 1 retry fails → exhausted
        var service = CreateServiceWithGateSequence(
            gateResults: [true, false, false],
            maxRetries: 1);

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.RetryCount.Should().Be(1);
        run.IsDraftPr.Should().BeTrue();
    }

    /// <summary>
    /// Initial QG uses all retries → cleanup → final QG fails → immediate draft PR (0 retries left).
    /// </summary>
    [Fact]
    public async Task AllRetriesUsedBeforeCleanup_FinalQgFails_ImmediateDraftPr()
    {
        // maxRetries=2: initial QG fail, retry1 fail, retry2 pass (2 retries used), cleanup, final QG fails → 0 left
        var service = CreateServiceWithGateSequence(
            gateResults: [false, false, true, false],
            maxRetries: 2);

        var run = await service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.RetryCount.Should().Be(2);
        run.IsDraftPr.Should().BeTrue();
    }

    /// <summary>
    /// Property: For any combination of initial retries used and final QG results,
    /// the total retry count never exceeds MaxRetries and the terminal state is correct.
    /// </summary>
    [Property]
    public void TotalRetryCount_NeverExceedsMaxRetries(
        PositiveInt maxRetriesRaw,
        NonNegativeInt initialFailsRaw,
        NonNegativeInt finalFailsRaw)
    {
        var maxRetries = (maxRetriesRaw.Get % 4) + 1; // 1-4
        var initialFails = initialFailsRaw.Get % (maxRetries + 1); // 0..maxRetries
        var finalFails = finalFailsRaw.Get % (maxRetries + 1); // 0..maxRetries

        // Build gate sequence: initialFails failures, then pass, then finalFails failures, then pass
        var gateResults = new List<bool>();
        for (var i = 0; i < initialFails; i++) gateResults.Add(false);
        gateResults.Add(true); // initial QG eventually passes
        for (var i = 0; i < finalFails; i++) gateResults.Add(false);
        gateResults.Add(true); // final QG eventually passes (may not be reached if budget exhausted)

        var service = CreateServiceWithGateSequence(gateResults.ToArray(), maxRetries);

        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Core property: total retries never exceed MaxRetries
        run.RetryCount.Should().BeLessThanOrEqualTo(maxRetries);

        // Terminal state is either Completed or Failed
        run.CurrentStep.Should().BeOneOf(PipelineStep.Completed, PipelineStep.Failed);

        if (run.CurrentStep == PipelineStep.Completed)
            run.IsDraftPr.Should().BeFalse();
    }

    // --- Factory methods ---

    private static PipelineOrchestrationService CreateService(
        bool allGatesPass, bool externalCiEnabled = false, bool cleanupProducesChanges = true)
    {
        var (configStore, factory, _, repoProvider, _, logger) = CreateBaseMocks();

        var config = TestPipelineConfig.Default();
        config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration
            {
                MaxRetries = config.Retry.MaxRetries,
                MaxAnalysisRetries = config.Retry.MaxAnalysisRetries,
                AgentTimeout = config.Retry.AgentTimeout,
                StallWarningInterval = config.Retry.StallWarningInterval,
                StallPollInterval = config.Retry.StallPollInterval,
            },
            IssuePageSize = config.IssuePageSize,
            Workspace = new WorkspaceConfiguration
            {
                WorkspaceBaseDirectory = config.Workspace.WorkspaceBaseDirectory,
                FailedWorkspaceRetentionDays = config.Workspace.FailedWorkspaceRetentionDays,
            },
            CodeReview = new CodeReviewConfiguration { Enabled = false },
            ExternalCi = new ExternalCiConfiguration
            {
                Enabled = externalCiEnabled,
                Timeout = config.ExternalCi.Timeout,
                PollInterval = config.ExternalCi.PollInterval,
            },
            Commit = new CommitConfiguration
            {
                BlacklistedPaths = config.Commit.BlacklistedPaths,
                BlacklistMode = config.Commit.BlacklistMode,
            },
            ClosedLoop = new ClosedLoopConfiguration
            {
                PollInterval = config.ClosedLoop.PollInterval,
                MaxRunsPerCycle = config.ClosedLoop.MaxRunsPerCycle,
                MaxConsecutivePollFailures = config.ClosedLoop.MaxConsecutivePollFailures,
                MaxBackoffInterval = config.ClosedLoop.MaxBackoffInterval,
            },
        };

        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        if (!cleanupProducesChanges)
        {
            // After cleanup, CommitAllAsync throws "No changes to commit" to simulate no-change cleanup
            var commitCallCount = 0;
            repoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var idx = Interlocked.Increment(ref commitCallCount);
                    if (idx > 1)
                        throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");
                    return Array.Empty<string>() as IReadOnlyList<string>;
                });
            repoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, IReadOnlyList<string>? _, bool allowEmpty, CancellationToken _) =>
                {
                    var idx = Interlocked.Increment(ref commitCallCount);
                    if (idx > 1 && !allowEmpty)
                        throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");
                    return Array.Empty<string>() as IReadOnlyList<string>;
                });
        }

        var mockValidator = new Mock<IQualityGateValidator>();
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = allGatesPass, Details = allGatesPass ? "OK" : "Failed" },
                Tests = new GateResult { GateName = "Tests", Passed = allGatesPass, Details = allGatesPass ? "OK" : "Failed" }
            });

        if (externalCiEnabled)
        {
            var mockPipelineProvider = new Mock<IPipelineProvider>();
            mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineRunStatus
                {
                    State = PipelineRunState.Passed,
                    Jobs = new List<PipelineJobResult>()
                });

            factory.Setup(f => f.CreatePipelineProvider(It.IsAny<ProviderConfig>()))
                .Returns(mockPipelineProvider.Object);

            configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProviderConfig>
                {
                    new() { Id = "pipeline-1", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "Test" }
                });
        }

        return new PipelineOrchestrationService(
            configStore.Object,
            factory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(logger.Object),
            new QualityGateOrchestrator(mockValidator.Object, new PullRequestOrchestrator(logger.Object), logger.Object),
            logger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }

    private static PipelineOrchestrationService CreateServiceWithGateSequence(bool[] gateResults, int maxRetries)
    {
        var (configStore, factory, _, _, _, logger) = CreateBaseMocks();

        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                Retry = new RetryConfiguration { MaxRetries = maxRetries },
                Workspace = new WorkspaceConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
                CodeReview = new CodeReviewConfiguration { Enabled = false }
            });

        var mockValidator = new Mock<IQualityGateValidator>();
        var callIndex = 0;
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callIndex) - 1;
                var passed = idx < gateResults.Length && gateResults[idx];
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = passed, Details = passed ? "OK" : $"Build failed: attempt {idx}" },
                    Tests = new GateResult { GateName = "Tests", Passed = true, Details = "Tests passed" }
                };
            });

        return new PipelineOrchestrationService(
            configStore.Object,
            factory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(logger.Object),
            new QualityGateOrchestrator(mockValidator.Object, new PullRequestOrchestrator(logger.Object), logger.Object),
            logger.Object,
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
        mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<QualityGateConfiguration>
                {
                    new() { Id = "default", DisplayName = "Default", CompilationCommand = "dotnet", CompilationArguments = ["build"], TestCommand = "dotnet", TestArguments = ["test"], Enabled = true }
                });
        mockConfigStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ReviewerConfiguration>());

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
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");
        mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);
        mockRepoProvider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("abc123");

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