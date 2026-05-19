using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PullRequestFinalizationServiceTests
{
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly PullRequestFinalizationService _sut;

    public PullRequestFinalizationServiceTests()
    {
        _sut = new PullRequestFinalizationService(_logger.Object);
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-1",
        IssueIdentifier = "test/repo#1",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        RepositoryName = "org/repo",
        WorkspacePath = "/tmp/workspace",
        StartedAt = DateTime.UtcNow.AddMinutes(-5)
    };

    // ── RunReflectionAsync ──

    [Fact]
    public async Task RunReflectionAsync_ExecutesAgentAndAccumulatesTokens()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var emitted = new List<string>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["done"], Usage = new TokenUsage { InputTokens = 80, OutputTokens = 20 }, Cost = 0.01m });

        await _sut.RunReflectionAsync(run, agentProvider.Object, config, emitted.Add, CancellationToken.None);

        agentProvider.Verify(a => a.ExecuteAsync(It.Is<AgentRequest>(r => r.UseResume && r.WorkspacePath == run.WorkspacePath), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()), Times.Once);
        run.TotalTokens.Should().BeGreaterThan(0);
        emitted.Should().Contain("🧠 Reflecting on run and updating brain knowledge...");
    }

    [Fact]
    public async Task RunReflectionAsync_OnFailure_DoesNotThrow()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"));

        await _sut.RunReflectionAsync(run, agentProvider.Object, config, _ => { }, CancellationToken.None);

        // Should not throw — just logs warning
        run.TotalTokens.Should().Be(0);
    }

    // ── SyncBrainPostRunAsync ──

    [Fact]
    public async Task SyncBrainPostRunAsync_DelegatesToBrainSync()
    {
        var run = CreateRun();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { BrainPushMaxRetries = 2 };

        await _sut.SyncBrainPostRunAsync(run, brainSync.Object, brainProvider.Object, config, _ => { }, CancellationToken.None);

        brainSync.Verify(b => b.SyncPostRunAsync(run, brainProvider.Object, It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), 2), Times.Once);
    }

    [Fact]
    public async Task SyncBrainPostRunAsync_OnFailure_SetsBrainUpdatesPushedFalse()
    {
        var run = CreateRun();
        run.BrainUpdatesPushed = true;
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { BrainPushMaxRetries = 2 };

        brainSync.Setup(b => b.SyncPostRunAsync(It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("push failed"));

        await _sut.SyncBrainPostRunAsync(run, brainSync.Object, brainProvider.Object, config, _ => { }, CancellationToken.None);

        run.BrainUpdatesPushed.Should().BeFalse();
    }

    // ── CollectFeedbackAsync ──

    [Fact]
    public async Task CollectFeedbackAsync_ParsesFeedbackFromAgent()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var emitted = new List<string>();

        historyService.Setup(h => h.GetRunHistory()).Returns([]);
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"testing","comment":"good"},"issue":{"rating":5,"category":"feature","comment":"clear"}}"""] });

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, historyService.Object, emitted.Add, CancellationToken.None);

        run.Feedback.Should().NotBeNull();
        emitted.Should().Contain("📋 Collecting run feedback...");
    }

    [Fact]
    public async Task CollectFeedbackAsync_OnFailure_CreatesFallback()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new InvalidOperationException("timeout"));

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, null, _ => { }, CancellationToken.None);

        run.Feedback.Should().NotBeNull();
        run.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
    }

    [Fact]
    public async Task CollectFeedbackAsync_NullHistoryService_HandlesGracefully()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":3,"category":"infra","comment":"ok"}}"""] });

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, null, _ => { }, CancellationToken.None);

        run.Feedback.Should().NotBeNull();
    }
}
