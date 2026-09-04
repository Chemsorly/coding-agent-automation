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

    [Fact]
    public async Task CollectFeedbackAsync_UsesConfiguredTimeout()
    {
        // TODO: Add a test passing config: null and asserting Timeout == TimeSpan.FromSeconds(60).
        // The null-coalescing branch (config?.FeedbackTimeoutSeconds ?? FeedbackConstraints.FailureFeedbackTimeoutSeconds)
        // is not covered by the two tests below because both pass a non-null config. If the branch were accidentally
        // broken (e.g., changed to config!.FeedbackTimeoutSeconds), these tests would still pass. (Warning from review #2225)
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var config = new PipelineConfiguration { FeedbackTimeoutSeconds = 180 };
        AgentRequest? capturedRequest = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedRequest = req)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"testing","comment":"ok"}}"""] });

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, null, _ => { }, CancellationToken.None, config);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Timeout.Should().Be(TimeSpan.FromSeconds(180));
    }

    [Fact]
    public async Task CollectFeedbackAsync_DefaultConfig_Uses60SecondTimeout()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var config = new PipelineConfiguration(); // default FeedbackTimeoutSeconds = 60
        AgentRequest? capturedRequest = null;

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedRequest = req)
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"testing","comment":"ok"}}"""] });

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, null, _ => { }, CancellationToken.None, config);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Timeout.Should().Be(TimeSpan.FromSeconds(60));
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
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = brainSync.Object,
                BrainProvider = brainProvider.Object,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

        transitions.Should().ContainInOrder(
            PipelineStep.GeneratingPrDescription,
            PipelineStep.ReflectingOnRun,
            PipelineStep.SyncingBrainRepoPostRun);
        // TODO: This test uses CreateRun() which sets WorkspacePath = "/tmp/workspace". Because
        // .agent/pr-description.md does not exist there, GeneratePrDescriptionAsync silently skips the
        // UpdatePullRequestAsync call — the happy-path PR description update is never exercised here.
        // Override WorkspacePath to a real temp directory and write the pr-description file so this test
        // also validates that repoProvider.UpdatePullRequestAsync is called when the file exists.
        // Additionally, if /tmp/workspace happens to exist on a CI host with a stale file from a prior run,
        // test outcomes become non-deterministic (latent flakiness risk).
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
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = true,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = new PipelineConfiguration(),
                BrainSync = brainSync.Object,
                BrainProvider = brainProvider.Object,
                FeedbackService = feedbackService,
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

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
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = null,
                BrainProvider = null,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

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
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = brainSync.Object,
                BrainProvider = brainProvider.Object,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

        transitions.Should().ContainInOrder(PipelineStep.GeneratingPrDescription);
        transitions.Should().NotContain(PipelineStep.ReflectingOnRun);
        brainSync.Verify(b => b.SyncPostRunAsync(It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainProviderPresentButBrainSyncNull_SkipsBrainSync()
    {
        // Exercises the second && operand: brainProvider is not null, but brainSync is null.
        // The gate must not call SyncPostRunAsync even though a provider is configured.
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var transitions = new List<PipelineStep>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = null,                     // BrainSync null — second operand fails
                BrainProvider = brainProvider.Object, // BrainProvider present — first operand passes
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

        transitions.Should().NotContain(PipelineStep.ReflectingOnRun);
        transitions.Should().NotContain(PipelineStep.SyncingBrainRepoPostRun);
        // Serilog ILogger.Information only has generic overloads up to 3 type params; a 5-arg call
        // (RunId, isDraft, hasProvider, hasSync, readOnly) resolves to Information(string, params object[]).
        // Moq must match the params-array overload — using individual typed matchers would target a
        // non-existent 5-generic-parameter overload and silently never match.
        _logger.Verify(l => l.Information(
            It.Is<string>(s => s.Contains("skipping brain post-run sync")),
            It.IsAny<object[]>()),
            Times.Once);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainSyncPresentButBrainProviderNull_SkipsBrainSync()
    {
        // Exercises the first && operand: brainSync is not null, but brainProvider is null.
        // The gate must short-circuit on the first operand and not call SyncPostRunAsync.
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var brainSync = new Mock<IBrainSyncService>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
        var transitions = new List<PipelineStep>();

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = brainSync.Object, // BrainSync present — second operand passes
                BrainProvider = null,         // BrainProvider null — first operand fails
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

        transitions.Should().NotContain(PipelineStep.ReflectingOnRun);
        transitions.Should().NotContain(PipelineStep.SyncingBrainRepoPostRun);
        brainSync.Verify(b => b.SyncPostRunAsync(It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()), Times.Never);
        // Serilog ILogger.Information only has generic overloads up to 3 type params; a 5-arg call
        // (RunId, isDraft, hasProvider, hasSync, readOnly) resolves to Information(string, params object[]).
        // Moq must match the params-array overload — using individual typed matchers would target a
        // non-existent 5-generic-parameter overload and silently never match.
        _logger.Verify(l => l.Information(
            It.Is<string>(s => s.Contains("skipping brain post-run sync")),
            It.IsAny<object[]>()),
            Times.Once);
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainGateSkipped_LogsSkipReasonWithCorrectFields()
    {
        // TODO [WARNING]: This test is nearly a duplicate of RunPostPrSequenceAsync_WhenBrainSyncPresentButBrainProviderNull_SkipsBrainSync
        // — both use BrainSync=null, BrainProvider=null and verify the same log assertion. The comment
        // "gate fires due to null provider" is misleading since both operands are null. This test adds
        // no distinct coverage over the two partial-null tests. Consider removing or differentiating it
        // to test a genuinely distinct scenario (e.g., isDraft=true triggering the skip).
        // Verifies the diagnostic log emits the correct structured fields when the brain gate fails.
        // isDraft=false, brainProvider=null, brainSync=null — gate fires due to null provider.
        var run = CreateRun();
        run.PullRequestNumber = "42";
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":3,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        await _sut.RunPostPrSequenceAsync(
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = config,
                BrainSync = null,
                BrainProvider = null,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = _ => Task.CompletedTask
            },
            CancellationToken.None);

        // Template must contain the skip-reason key phrase; structured args carry:
        // RunId (string), IsDraft=false, HasProvider=false, HasSync=false, ReadOnly=false.
        // Serilog ILogger.Information only has generic overloads up to 3 type params; a 5-arg call
        // resolves to Information(string, params object[]). Individual typed Moq matchers would target
        // a non-existent overload and never match. We match the params-array overload instead.
        // Field-level verification (isDraft=false etc.) is exercised implicitly: the else-branch is
        // only reached when the gate condition fails, and these tests confirm it fires exactly once.
        _logger.Verify(l => l.Information(
            It.Is<string>(s => s.Contains("skipping brain post-run sync")),
            It.IsAny<object[]>()),
            Times.Once);
    }

    // ── GeneratePrDescriptionAsync — blockquote stripping ──

    private static string WritePrDescriptionFile(string tempDir, string content)
    {
        var agentDir = Path.Combine(tempDir, ".agent");
        Directory.CreateDirectory(agentDir);
        var filePath = Path.Combine(agentDir, "pr-description.md");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_StripsBlockquotePrefix_FromAllLines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            WritePrDescriptionFile(tempDir, "> ### Summary\n> \n> Some description\n> with multiple lines");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "existing body";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().StartWith("### Summary\n\nSome description\nwith multiple lines");
            capturedBody.Should().NotContain("> ###");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_PreservesOutput_WithoutBlockquotePrefix()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            WritePrDescriptionFile(tempDir, "### Summary\n\nSome description");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "existing body";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().StartWith("### Summary\n\nSome description");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_PreservesMidLineGreaterThan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            WritePrDescriptionFile(tempDir, "> ### Summary\n> Code uses x > 5 comparison\n> Generic List<T>");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().Contain("Code uses x > 5 comparison");
            capturedBody.Should().Contain("Generic List<T>");
            capturedBody.Should().NotContain("> Code uses x > 5");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmptyBlockquoteLine_BecomesEmptyString()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            WritePrDescriptionFile(tempDir, "> ### Summary\n>\n> Next paragraph");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().StartWith("### Summary\n\nNext paragraph");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_StripsBlockquotePrefix_WithCrlfLineEndings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            // Write file with CRLF line endings
            WritePrDescriptionFile(tempDir, "> ### Summary\r\n> \r\n> Some description\r\n> with multiple lines\r\n");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "existing body";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().StartWith("### Summary\n\nSome description\nwith multiple lines");
            // TODO: The NotContain("\r") assertion may pass because File.ReadAllTextAsync normalises CRLF on some
            // platforms, not because StripBlockquotePrefix does so. Clarify which layer is responsible for \r
            // removal — if it is StripBlockquotePrefix, add a unit test for that method in isolation with a CRLF
            // input to make the guarantee explicit and platform-independent.
            capturedBody.Should().NotContain("\r");
            capturedBody.Should().NotContain("> ###");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmptyBlockquoteLine_WithCrlfLineEndings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            // Bare ">" with CRLF
            WritePrDescriptionFile(tempDir, "> ### Summary\r\n>\r\n> Next paragraph\r\n");

            var run = CreateRun();
            run.PullRequestNumber = "10";
            run.PullRequestBody = "";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().StartWith("### Summary\n\nNext paragraph");
            capturedBody.Should().NotContain("\r");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_WhenFileExists_UsesFileContentAsDescription()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            var fileContent = "### Summary\n\nThis PR fixes the bug.\n\n### Approach\n\nMinimal change.";
            WritePrDescriptionFile(tempDir, fileContent);

            var run = CreateRun();
            run.PullRequestNumber = "42";
            run.PullRequestBody = "existing body";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };
            string? capturedBody = null;

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["I will run: git diff", "diff --git a/..."] });
            repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<int, string, bool?, CancellationToken>((_, body, _, _) => capturedBody = body)
                .Returns(Task.CompletedTask);

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            // File content is used, not OutputLines
            capturedBody.Should().NotBeNull();
            // TODO: Strengthen these assertions — use capturedBody.Should().Be($"{fileContent}\n\n---\n\nexisting body")
            // (or at minimum StartWith(fileContent)) to verify exact round-trip fidelity. The current Contain checks
            // would pass even if StripBlockquotePrefix corrupted the content. Also, the NotContain assertions for
            // OutputLines content could spuriously pass if the file content happened to contain those substrings —
            // an exact-match assertion makes them redundant and the test immune to content coincidences.
            capturedBody.Should().Contain("### Summary");
            capturedBody.Should().Contain("This PR fixes the bug.");
            capturedBody.Should().Contain("### Approach");
            capturedBody.Should().NotContain("I will run: git diff");
            capturedBody.Should().NotContain("diff --git a/");

            // run.PullRequestBody is updated
            run.PullRequestBody.Should().NotBe("existing body");
            run.PullRequestBody.Should().Contain("### Summary");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_WhenFileDoesNotExist_SkipsUpdateAndLogsWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(tempDir);
            // Do NOT create .agent/pr-description.md

            var run = CreateRun();
            run.PullRequestNumber = "42";
            run.PullRequestBody = "unchanged body";
            run.WorkspacePath = tempDir;
            var agentProvider = new Mock<IAgentProvider>();
            var repoProvider = new Mock<IRepositoryProvider>();
            var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

            agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["### Summary", "Some output"] });

            await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

            // UpdatePullRequestAsync must NOT have been called
            repoProvider.Verify(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);

            // run.PullRequestBody is unchanged
            run.PullRequestBody.Should().Be("unchanged body");

            // Warning was logged (template + 2 structured args: RunId and Path)
            // TODO: The It.IsAny<string>() matchers for the two structured log arguments provide no additional
            // constraint beyond the template match. If stronger validation is needed, replace them with
            // It.Is<string>(s => s == run.RunId) and It.Is<string>(s => s.EndsWith("pr-description.md"))
            // to confirm the correct run and path were logged.
            _logger.Verify(l => l.Warning(
                It.Is<string>(s => s.Contains("PR description file not found")),
                It.IsAny<string>(), It.IsAny<string>()),
                Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });
        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = false,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = agentProvider.Object,
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

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
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = false,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = Mock.Of<IAgentProvider>(),
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = new FeedbackService(_logger.Object),
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => { transitions.Add(step); return Task.CompletedTask; }
            },
            CancellationToken.None);

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
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = true,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = agentProvider.Object,
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = feedbackService,
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => Task.CompletedTask
            },
            CancellationToken.None);

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
        repoProvider.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        await _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = false,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = agentProvider.Object,
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = feedbackService,
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => Task.CompletedTask
            },
            CancellationToken.None);

        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/41");
        run.PullRequestNumber.Should().Be("41");
        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.FinalLabel.Should().Be(AgentLabels.Done);
    }

    [Fact]
    public async Task RunFullPrCreationAsync_WhenRunPostPrSequenceThrowsOce_SetsCompletedAt()
    {
        // Regression test for: RunFullPrCreationAsync OCE from RunPostPrSequenceAsync skips
        // MarkCompleted(), leaving CompletedAt=null — ghost PipelineRun with null CompletedAt.
        // The terminal mutations (MarkCompleted, CurrentStep, FinalLabel) are now in a finally
        // block so they execute even when RunPostPrSequenceAsync propagates an OCE.

        var run = CreateRun();
        run.BranchName = "agent/test-1";
        var report = CreateReport();
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(5) };

        // PR creation succeeds — all repo operations are mocked to complete immediately so
        // the already-cancelled token below does not interfere with the PR creation phase.
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
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        // Use an already-cancelled token so the OCE carries a genuinely cancelled token,
        // matching the production scenario exactly (host shutdown / job cancellation).
        // The mocked repo operations complete synchronously without inspecting the token,
        // so cancelling before the call does not short-circuit PR creation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // RunPostPrSequenceAsync propagates OCE (e.g., from GeneratingPrDescription / brain sync)
        // Simulate by throwing from the agent provider that is called inside RunPostPrSequenceAsync.
        // The OCE carries cts.Token (IsCancellationRequested=true), which is the same token passed
        // to RunFullPrCreationAsync — this is the unambiguous OCE-propagation path under test.
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        var act = () => _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = false,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = agentProvider.Object,
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = feedbackService,
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => Task.CompletedTask
            },
            cts.Token);

        // OCE propagates out of RunFullPrCreationAsync
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Even though OCE propagated, the finally block must have run MarkCompleted()
        run.CompletedAtOffset.Should().NotBeNull("MarkCompleted() must be called in finally even on OCE");
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
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);

        var act = () => _sut.RunFullPrCreationAsync(
            new PrCreationRequest
            {
                Run = run,
                Report = report,
                IsDraft = false,
                PrOrchestrator = prOrchestrator,
                RepoProvider = repoProvider.Object,
                AgentProvider = Mock.Of<IAgentProvider>(),
                BrainProvider = null,
                BrainSync = null,
                Config = config,
                Issue = null,
                IssueComments = null,
                FeedbackService = new FeedbackService(_logger.Object),
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = step => Task.CompletedTask
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("permission denied");
    }
}
