using FluentAssertions;
using FsCheck.Xunit;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for pipeline state transitions.
/// </summary>
public class PipelineStateTransitionPropertyTests
{
    /// <summary>
    /// Property 7: Pipeline step transitions update in-memory state.
    /// Verifies that OnChange fires on each transition and ActiveRun.CurrentStep
    /// matches the expected step after each transition in the pipeline flow.
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public void PipelineTransitions_UpdateStateAndFireOnChange(bool shouldCancel)
    {
        var transitionLog = new List<PipelineStep>();
        var service = CreateServiceWithMocks(allGatesPass: !shouldCancel);

        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                transitionLog.Add(service.ActiveRun.CurrentStep);
        };

        // Start the pipeline
        var run = service.StartPipelineAsync("issue-1", "repo-1", "42", CancellationToken.None)
            .GetAwaiter().GetResult();

        // After start, should be in WaitingForChat
        run.CurrentStep.Should().Be(PipelineStep.WaitingForChat);

        // Verify the expected transitions occurred in order
        transitionLog.Should().ContainInOrder(
            PipelineStep.CloningRepository,
            PipelineStep.CreatingBranch,
            PipelineStep.GeneratingCode,
            PipelineStep.WaitingForChat);

        // OnChange should have fired at least once per transition
        transitionLog.Count.Should().BeGreaterThanOrEqualTo(4);

        if (shouldCancel)
        {
            // Cancel the pipeline
            service.CancelPipelineAsync().GetAwaiter().GetResult();
            run.CurrentStep.Should().Be(PipelineStep.Cancelled);
            transitionLog.Should().Contain(PipelineStep.Cancelled);
        }
        else
        {
            // Proceed to quality gates (which pass)
            service.ProceedToQualityGatesAsync(CancellationToken.None).GetAwaiter().GetResult();

            // Should have transitioned through RunningQualityGates → CreatingPullRequest → Completed
            transitionLog.Should().Contain(PipelineStep.RunningQualityGates);
            transitionLog.Should().Contain(PipelineStep.CreatingPullRequest);
            transitionLog.Should().Contain(PipelineStep.Completed);
            run.CurrentStep.Should().Be(PipelineStep.Completed);
        }
    }

    private static PipelineOrchestrationService CreateServiceWithMocks(bool allGatesPass)
    {
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
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

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.CloneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreateBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("feature/auto-42-test");
        mockRepoProvider.Setup(p => p.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockRepoProvider.Setup(p => p.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://github.com/test/pr/1");

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        mockAgentProvider.Setup(p => p.ExecuteWithResumeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

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
            mockLogger.Object,
            runsDirectory: Path.Combine(Path.GetTempPath(), $"test-runs-{Guid.NewGuid()}"));
    }
}
