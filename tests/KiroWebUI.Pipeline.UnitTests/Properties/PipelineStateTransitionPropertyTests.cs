using AwesomeAssertions;
using FsCheck.Xunit;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Pipeline.UnitTests.Helpers;

namespace KiroWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for pipeline state transitions.
/// </summary>
public class PipelineStateTransitionPropertyTests
{
    /// <summary>
    /// Property 7: Pipeline step transitions update in-memory state.
    /// Verifies that OnChange fires on each transition and ActiveRun.CurrentStep
    /// matches the expected step after each transition in the pipeline flow.
    /// The pipeline now runs end-to-end without pausing.
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void PipelineTransitions_UpdateStateAndFireOnChange(bool allGatesPass)
    {
        var transitionLog = new List<PipelineStep>();
        var service = CreateServiceWithMocks(allGatesPass: allGatesPass);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        // Start the pipeline — runs end-to-end
        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", "agent-1", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Verify the expected transitions occurred in order
        transitionLog.Should().ContainInOrder(
            PipelineStep.CloningRepository,
            PipelineStep.CreatingBranch,
            PipelineStep.AnalyzingCode,
            PipelineStep.PostingAnalysis,
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates);

        // OnChange should have fired at least once per transition
        transitionLog.Count.Should().BeGreaterThanOrEqualTo(6);

        if (allGatesPass)
        {
            // Should have transitioned through CreatingPullRequest → Completed
            transitionLog.Should().Contain(PipelineStep.CreatingPullRequest);
            transitionLog.Should().Contain(PipelineStep.Completed);
            run.CurrentStep.Should().Be(PipelineStep.Completed);
        }
        else
        {
            // Quality gates failed, retries exhausted → Failed
            run.CurrentStep.Should().Be(PipelineStep.Failed);
            transitionLog.Should().Contain(PipelineStep.Failed);
        }
    }

    private static PipelineOrchestrationService CreateServiceWithMocks(bool allGatesPass)
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
        var mockValidator = new Mock<IQualityGateValidator>();
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = allGatesPass, Details = allGatesPass ? "OK" : "Failed" },
                Tests = new GateResult { GateName = "Tests", Passed = allGatesPass, Details = allGatesPass ? "OK" : "Failed" }
            });

        return new PipelineOrchestrationService(
            mockConfigStore.Object,
            mockFactory.Object,
            new IssueDescriptionParser(),
            mockValidator.Object,
            new CiLogWriter(mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }
}
