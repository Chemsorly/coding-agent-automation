using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for pipeline retry logic.
/// </summary>
public class PipelineRetryPropertyTests
{
    /// <summary>
    /// Property 4: Retry logic enforces max count and accumulates all errors.
    /// For any max retry count N (1-5) and any sequence of N distinct error messages,
    /// when quality gates fail on every attempt, the orchestrator retries exactly N times
    /// and RetryErrors contains all N error messages in order.
    /// **Validates: Requirements 5.2, 5.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public void RetryLogic_EnforcesMaxCountAndAccumulatesErrors(PositiveInt retryCountRaw)
    {
        // Clamp to 1-5 for test speed
        var maxRetries = (retryCountRaw.Get % 5) + 1;

        // Generate unique error messages for each quality gate failure
        var errorMessages = Enumerable.Range(0, maxRetries + 1)
            .Select(i => $"Build failed with exit code 1: Error-{i}")
            .ToList();

        var service = CreateServiceWithFailingQualityGates(maxRetries, errorMessages);

        // Start the pipeline — should reach WaitingForAnalysisApproval
        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None)
            .GetAwaiter().GetResult();
        run.CurrentStep.Should().Be(PipelineStep.WaitingForAnalysisApproval);

        // Approve analysis to continue to code generation
        service.ApproveAnalysisAsync(CancellationToken.None).GetAwaiter().GetResult();
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        // Simulate retry cycles: proceed to quality gates (which always fail)
        // First call is the initial attempt, subsequent calls are retries
        // Total calls needed: maxRetries + 1 (initial + maxRetries retries)
        // But the last call (when retries exhausted) creates a draft PR
        for (var i = 0; i <= maxRetries; i++)
        {
            service.ProceedToQualityGatesAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (i < maxRetries)
            {
                // Retries still available — should be back in WaitingForChat
                run.CurrentStep.Should().Be(PipelineStep.WaitingForChat,
                    $"after attempt {i + 1}/{maxRetries + 1}, should be WaitingForChat");
                run.RetryCount.Should().Be(i + 1);

                // Send a follow-up message to simulate user interaction before next attempt
                service.SendChatMessageAsync("fix the errors", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }

        // After max retries exhausted: draft PR created, run marked Failed
        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.RetryCount.Should().Be(maxRetries);
        // RetryErrors: one per failed attempt (maxRetries from retry attempts + 1 from final exhausted attempt)
        run.RetryErrors.Should().HaveCount(maxRetries + 1);

        // All error messages accumulated in order
        var retryErrorsList = run.RetryErrors.Reverse().ToList(); // ConcurrentBag is LIFO, reverse for insertion order
        for (var i = 0; i <= maxRetries; i++)
        {
            retryErrorsList[i].Should().Contain($"Error-{i}");
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
                WorkspaceBaseDirectory = Path.GetTempPath()
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
                Labels = Array.Empty<string>(), AcceptanceCriteria = Array.Empty<string>()
            });
        mockIssueProvider.Setup(p => p.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("feature/auto-42-test");
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/test/pr/1");

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

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

        return new PipelineOrchestrationService(
            mockConfigStore.Object,
            mockFactory.Object,
            new IssueDescriptionParser(),
            mockValidator.Object,
            mockLogger.Object,
            runsDirectory: Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));
    }
}
