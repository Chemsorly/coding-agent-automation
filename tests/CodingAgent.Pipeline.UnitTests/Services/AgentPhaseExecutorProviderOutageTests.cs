using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// A model provider outage during analysis is an infrastructure failure, not a problem with the issue.
/// </summary>
public class AgentPhaseExecutorProviderOutageTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"analysis-outage-{Guid.NewGuid():N}");

    public AgentPhaseExecutorProviderOutageTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(AgentErrorCategory.ProviderRateLimit)]
    [InlineData(AgentErrorCategory.ProviderOverload)]
    [InlineData(AgentErrorCategory.PermanentAuthFailure)]
    public async Task ProviderOutageDuringAnalysis_FailsAsInfrastructure_WithoutRetryingOrLabellingTheIssue(AgentErrorCategory category)
    {
        var agent = new Mock<IAgentProvider>();
        var issueOps = new Mock<IAgentIssueOperations>();
        var callbacks = new Mock<IPipelineCallbacks>();
        agent.Setup(a => a.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = true, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        agent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        agent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 1, OutputLines = ["provider said no"], ErrorCategory = category });

        var run = new PipelineRun
        {
            RunId = "outage", IssueIdentifier = "42", IssueTitle = "t", IssueProviderConfigId = "ip", RepoProviderConfigId = "rp",
            WorkspacePath = _workspacePath
        };
        var context = new AgentPhaseContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10), StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1), MaxAnalysisRetries = 2, AnalysisReviewEnabled = false
            },
            AgentProvider = agent.Object, IssueOps = issueOps.Object, Callbacks = callbacks.Object, OrchestratorCts = null,
            Issue = new IssueDetail { Identifier = "42", Title = "t", Description = "d", Labels = ["bug"] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "r", AcceptanceCriteria = ["a"] }
        };

        var proceed = await new AgentPhaseExecutor(new Mock<Serilog.ILogger>().Object)
            .ExecuteAnalysisPhaseAsync(context, Array.Empty<IssueComment>(), false, CancellationToken.None);

        proceed.Should().BeFalse();
        run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure);
        issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Never);
        agent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once,
            "retrying at once cannot help while the provider is unavailable");
        callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }
}
