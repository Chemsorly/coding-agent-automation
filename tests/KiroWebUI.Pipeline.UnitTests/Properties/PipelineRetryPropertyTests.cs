using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Pipeline.UnitTests.Helpers;

namespace KiroWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for pipeline retry logic.
/// </summary>
public class PipelineRetryPropertyTests
{
    /// <summary>
    /// Property 4: Retry logic enforces max count and accumulates all errors.
    /// For any max retry count N (1-5) and any sequence of N distinct error messages,
    /// when quality gates fail on every attempt, the orchestrator auto-retries exactly N times
    /// (sending fix prompts to the agent) and RetryErrors contains all N+1 error messages.
    /// The pipeline now runs end-to-end without pausing.
    /// **Validates: Requirements 5.2, 5.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void RetryLogic_EnforcesMaxCountAndAccumulatesErrors(PositiveInt retryCountRaw)
    {
        // Clamp to 1-5 for test speed
        var maxRetries = (retryCountRaw.Get % 5) + 1;

        // Generate unique error messages for each quality gate failure
        var errorMessages = Enumerable.Range(0, maxRetries + 1)
            .Select(i => $"Build failed with exit code 1: Error-{i}")
            .ToList();

        var service = CreateServiceWithFailingQualityGates(maxRetries, errorMessages);

        // Start the pipeline — runs end-to-end including auto-retry loop
        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        // After max retries exhausted: draft PR created, run marked Failed
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.RetryCount.Should().Be(maxRetries);
        // RetryErrors: one per failed attempt (initial + maxRetries retries)
        run.RetryErrors.Should().HaveCount(maxRetries + 1);

        // All error messages accumulated (ConcurrentBag does not guarantee order)
        for (var i = 0; i <= maxRetries; i++)
        {
            run.RetryErrors.Should().Contain(e => e.Contains($"Error-{i}"));
        }

        // Run history should contain the completed run
        service.GetRunHistory().Should().HaveCount(1);
        service.GetRunHistory()[0].FinalStep.Should().Be(PipelineStep.Failed);
    }

    private static PipelineOrchestrationService CreateServiceWithFailingQualityGates(
        int maxRetries, List<string> errorMessages)
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = false }
            });
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
        mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("feature/auto-42-test");
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/test/pr/1");
        mockRepoProvider.Setup(p => p.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockRepoProvider.Setup(p => p.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>() as IReadOnlyList<FileChangeSummary>);

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

        // Mock QualityGateValidator — always fails with unique error per call
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var callIndex = 0;
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callIndex) - 1;
                var errorMsg = idx < errorMessages.Count ? errorMessages[idx] : $"Error-{idx}";
                return new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = errorMsg },
                    Tests = new GateResult { GateName = "Tests", Passed = true, Details = "Tests passed" }
                };
            });

        var runHistory = new List<PipelineRunSummary>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistory()).Returns(() => runHistory.AsReadOnly());
        mockHistoryService.Setup(h => h.AddRunToHistory(It.IsAny<PipelineRun>()))
            .Callback<PipelineRun>(run => runHistory.Add(run.ToSummary()));

        return new PipelineOrchestrationService(
            mockConfigStore.Object,
            mockFactory.Object,
            new IssueDescriptionParser(),
            mockValidator.Object,
            new CiLogWriter(mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistoryService.Object);
    }
}
