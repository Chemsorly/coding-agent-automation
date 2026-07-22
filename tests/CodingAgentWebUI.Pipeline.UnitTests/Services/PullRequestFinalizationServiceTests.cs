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

    private static QualityGateReport CreateReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
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

        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);
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

    // ── RunPostPrSequenceAsync ──

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenNotDraft_ExecutesAllSteps()
    {
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var transitions = new List<PipelineStep>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            run, isDraft: false, agentProvider.Object, repoProvider.Object, config,
            brainSync.Object, brainProvider.Object, feedbackService, historyService.Object,
            _ => { }, step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingPrDescription,
            PipelineStep.ReflectingOnRun,
            PipelineStep.SyncingBrainRepoPostRun);
        // TODO: Verify which specific AgentRequest was made for each step (PR description vs reflection vs feedback) rather than just counting calls.
        // TODO: Assert observable side-effects (e.g., run.Feedback populated, repoProvider.UpdatePullRequestAsync invoked) to validate each step executed correctly.
        agentProvider.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()), Times.Exactly(3));
        brainSync.Verify(b => b.SyncPostRunAsync(run, brainProvider.Object, It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenDraft_SkipsAllSteps()
    {
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var transitions = new List<PipelineStep>();

        await _sut.RunPostPrSequenceAsync(
            run, isDraft: true, agentProvider.Object, repoProvider.Object,
            new PipelineConfiguration(), brainSync.Object, brainProvider.Object,
            feedbackService, null, _ => { },
            step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        transitions.Should().BeEmpty();
        agentProvider.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()), Times.Never);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenNoBrainProvider_SkipsReflectionAndBrainSync()
    {
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var transitions = new List<PipelineStep>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            run, isDraft: false, agentProvider.Object, repoProvider.Object, config,
            brainSync: null, brainProvider: null, feedbackService, historyService.Object,
            _ => { }, step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        transitions.Should().ContainInOrder(PipelineStep.GeneratingPrDescription);
        transitions.Should().NotContain(PipelineStep.ReflectingOnRun);
        transitions.Should().NotContain(PipelineStep.SyncingBrainRepoPostRun);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainReadOnly_SkipsReflectionAndBrainSync()
    {
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5), BrainReadOnly = true };
        var transitions = new List<PipelineStep>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            run, isDraft: false, agentProvider.Object, repoProvider.Object, config,
            brainSync.Object, brainProvider.Object, feedbackService, historyService.Object,
            _ => { }, step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        transitions.Should().ContainInOrder(PipelineStep.GeneratingPrDescription);
        transitions.Should().NotContain(PipelineStep.ReflectingOnRun);
        brainSync.Verify(b => b.SyncPostRunAsync(It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()), Times.Never);
    }

    // ── GeneratePrDescriptionAsync — blockquote stripping ──

    [Fact]
    public async Task GeneratePrDescriptionAsync_StripsBlockquotePrefix_FromAllLines()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "existing body";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["> ### Summary", "> ", "> Some description", "> with multiple lines"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().StartWith("### Summary\n\nSome description\nwith multiple lines");
        capturedBody.Should().NotContain("> ###");
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_PreservesOutput_WithoutBlockquotePrefix()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "existing body";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["### Summary", "", "Some description"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().StartWith("### Summary\n\nSome description");
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_PreservesMidLineGreaterThan()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["> ### Summary", "> Code uses x > 5 comparison", "> Generic List<T>"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("Code uses x > 5 comparison");
        capturedBody.Should().Contain("Generic List<T>");
        // TODO: Assertions are too weak — the substrings exist even without stripping the leading "> " prefix.
        // Should additionally assert capturedBody.Should().NotContain("> Code uses x > 5") to confirm prefix removal.
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmptyBlockquoteLine_BecomesEmptyString()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["> ### Summary", ">", "> Next paragraph"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().StartWith("### Summary\n\nNext paragraph");
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_StripsBlockquotePrefix_WithCrlfLineEndings()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "existing body";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        // Simulate CRLF: individual OutputLines contain trailing \r (as happens when upstream splits on \n only)
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["> ### Summary\r", "> \r", "> Some description\r", "> with multiple lines\r"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().StartWith("### Summary\n\nSome description\nwith multiple lines");
        capturedBody.Should().NotContain("\r");
        capturedBody.Should().NotContain("> ###");
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmptyBlockquoteLine_WithCrlfLineEndings()
    {
        var run = CreateRun();
        run.PullRequestNumber = "10";
        run.PullRequestBody = "";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        string? capturedBody = null;

        // Bare ">" with trailing \r simulates CRLF line endings
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["> ### Summary\r", ">\r", "> Next paragraph\r"] });
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().StartWith("### Summary\n\nNext paragraph");
        capturedBody.Should().NotContain("\r");
    }

    // ── RunFullPrCreationAsync ──

    [Fact]
    public async Task RunFullPrCreationAsync_HappyPath_CreatesPrAndSetsCompletedState()
    {
        var run = CreateRun();
        run.BranchName = "agent/test-1";
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var transitions = new List<PipelineStep>();

        // Setup PullRequestOrchestrator to succeed
        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoProvider.Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        repoProvider.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/pull/99");
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            run, report, isDraft: false, prOrchestrator, repoProvider.Object, agentProvider.Object,
            null, null, config, null, null, feedbackService, historyService.Object,
            _ => { }, step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.CompletedAtOffset.Should().NotBeNull();
        run.FinalLabel.Should().Be(AgentLabels.Done);
        run.FailureReason.Should().BeNull();
        transitions.Should().Contain(PipelineStep.CreatingPullRequest);
    }

    [Fact]
    public async Task RunFullPrCreationAsync_NoChanges_SetsFailedState()
    {
        var run = CreateRun();
        run.BranchName = "agent/test-1";
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration();
        var transitions = new List<PipelineStep>();

        // Setup PullRequestOrchestrator to return null (no commits ahead)
        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #1");

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            run, report, isDraft: false, prOrchestrator, repoProvider.Object, Mock.Of<IAgentProvider>(),
            null, null, config, null, null, new FeedbackService(_logger.Object), null,
            _ => { }, step => { transitions.Add(step); return Task.CompletedTask; }, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("Agent did not produce any changes. No commits ahead of base branch.");
        run.CompletedAtOffset.Should().NotBeNull();
        run.FinalLabel.Should().BeNull();
        transitions.Should().Contain(PipelineStep.CreatingPullRequest);
    }

    [Fact]
    public async Task RunFullPrCreationAsync_DraftPr_SetsFailedStateWithDraftMessage()
    {
        var run = CreateRun();
        run.BranchName = "agent/test-1";
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

        // Setup PullRequestOrchestrator to succeed
        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoProvider.Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        repoProvider.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/pull/99");
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            run, report, isDraft: true, prOrchestrator, repoProvider.Object, agentProvider.Object,
            null, null, config, null, null, feedbackService, null,
            _ => { }, step => Task.CompletedTask, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.FailureReason.Should().Be("Quality gates failed after max retries; draft PR created.");
        run.FinalLabel.Should().Be(AgentLabels.Error);
        run.CompletedAtOffset.Should().NotBeNull();
    }

    [Fact]
    public async Task RunFullPrCreationAsync_LinkedPr_SetsUrlAndNumberBeforeCallingOrchestrator()
    {
        var run = CreateRun();
        run.BranchName = "agent/test-1";
        run.LinkedPullRequest = new LinkedPullRequest
        {
            Url = "https://github.com/org/repo/pull/41",
            Number = 41,
            BranchName = "agent/issue-41",
            IsDraft = false
        };
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

        // Setup PullRequestOrchestrator to succeed (rework path — updates existing PR)
        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoProvider.Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            run, report, isDraft: false, prOrchestrator, repoProvider.Object, agentProvider.Object,
            null, null, config, null, null, feedbackService, null,
            _ => { }, step => Task.CompletedTask, CancellationToken.None);

        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/41");
        run.PullRequestNumber.Should().Be("41");
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.FinalLabel.Should().Be(AgentLabels.Done);
    }

    // TODO: This test only asserts exception propagation but does not verify that activity?.SetStatus(ActivityStatusCode.Error, ...) is called. Consider using a custom ActivityListener to assert telemetry decoration.
    [Fact]
    public async Task RunFullPrCreationAsync_ExceptionPropagates_WithTelemetryDecoration()
    {
        var run = CreateRun();
        run.BranchName = "agent/test-1";
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration();

        // Setup PullRequestOrchestrator to throw (push fails)
        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permission denied"));
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #1");

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        var act = () => _sut.RunFullPrCreationAsync(
            run, report, isDraft: false, prOrchestrator, repoProvider.Object, Mock.Of<IAgentProvider>(),
            null, null, config, null, null, new FeedbackService(_logger.Object), null,
            _ => { }, step => Task.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("permission denied");
    }
}
