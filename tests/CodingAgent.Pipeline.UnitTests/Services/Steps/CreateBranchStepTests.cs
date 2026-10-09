using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests for <see cref="CreateBranchStep"/> — specifically the PR-state guard added in issue #2954.
/// A rework or Review run whose PR is already merged or closed at run start must terminate
/// without attempting a git checkout and without <c>agent:error</c>.
/// </summary>
public class CreateBranchStepTests
{
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly CapturingSink _logSink = new();
    private readonly Serilog.ILogger _logger;
    private readonly List<string> _outputLines = [];
    private readonly List<PipelineStep> _transitions = [];

    public CreateBranchStepTests()
    {
        _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_logSink).CreateLogger();
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _outputLines.Add(line));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => _transitions.Add(step));
        _callbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── PrMerged guard ─────────────────────────────────────────────────────────

    /// <summary>
    /// When the PR is already merged at run start, CreateBranchStep must:
    /// - Return StepResult.Stop (no checkout)
    /// - Set run.CurrentStep = PrMerged
    /// - Not call CheckoutRemoteBranchAsync
    /// </summary>
    [Fact]
    public async Task WhenPrAlreadyMerged_StopsWithoutCheckout_AndSetsPrMergedStep()
    {
        const int prNum = 42;
        var (context, run) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);

        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Merged);

        var step = new CreateBranchStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop, "PR already merged — must stop without checkout");
        run.CurrentStep.Should().Be(PipelineStep.PrMerged);
        run.FinalLabel.Should().BeNull("merged run ends Succeeded with no error label");

        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "must not attempt git checkout when PR is already merged");

        _transitions.Should().Contain(PipelineStep.PrMerged,
            "must transition to PrMerged so orchestrator persists the correct terminal step");

        var emittedText = string.Join("\n", _outputLines);
        emittedText.Should().Contain($"#{prNum}").And.Contain("merged");
    }

    /// <summary>
    /// Same test for a Review run — the guard must fire for all run types that use CheckoutAndMergeAsync.
    /// </summary>
    // TODO [WARNING] (TestQualityReviewer): There is no corresponding test for PipelineRunType.Rework.
    // The acceptance criterion specifically targets "rework and Review runs". If CreateBranchStep ever
    // branches on RunType, a rework run could slip through. Add a test with PipelineRunType.Rework or
    // confirm the step does not branch on run type (in which case the existing tests are sufficient).
    [Fact]
    public async Task WhenPrAlreadyMerged_ReviewRun_StopsWithoutCheckout()
    {
        const int prNum = 99;
        var (context, run) = BuildContextWithLinkedPr(prNum, PipelineRunType.Review);

        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Merged);

        var step = new CreateBranchStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        run.CurrentStep.Should().Be(PipelineStep.PrMerged);

        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── PrClosed guard ─────────────────────────────────────────────────────────

    /// <summary>
    /// When the PR is closed without merge at run start, CreateBranchStep must:
    /// - Return StepResult.Stop (no checkout)
    /// - Set run.CurrentStep = PrClosed
    /// - Set run.FinalLabel = Cancelled
    /// - Not call CheckoutRemoteBranchAsync
    /// </summary>
    // TODO [WARNING] (TestQualityReviewer): WhenPrAlreadyClosed_StopsWithoutCheckout_AndSetsPrClosedStep only tests
    // PipelineRunType.Implementation. There is no corresponding WhenPrAlreadyClosed_ReviewRun_StopsWithoutCheckout test.
    // Given that WhenPrAlreadyMerged_ReviewRun_StopsWithoutCheckout exists for the merged path, the closed path should
    // have the same coverage for consistency and to catch any run-type-conditional branching.
    [Fact]
    public async Task WhenPrAlreadyClosed_StopsWithoutCheckout_AndSetsPrClosedStep()
    {
        const int prNum = 55;
        var (context, run) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);

        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Closed);

        var step = new CreateBranchStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop, "PR closed without merge — must stop without checkout");
        run.CurrentStep.Should().Be(PipelineStep.PrClosed);
        run.FinalLabel.Should().Be(AgentLabels.Cancelled, "closed PR run ends Cancelled");

        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "must not attempt git checkout when PR is closed without merge");

        _transitions.Should().Contain(PipelineStep.PrClosed);

        var emittedText = string.Join("\n", _outputLines);
        emittedText.Should().Contain($"#{prNum}").And.Contain("closed");
    }

    // ── Fail-open guard ────────────────────────────────────────────────────────

    /// <summary>
    /// When GetPullRequestStateAsync throws, the step must fail-open and proceed with checkout.
    /// This prevents a transient provider error from blocking all rework/review runs.
    /// </summary>
    [Fact]
    public async Task WhenPrStateQueryThrows_FailOpen_ProceedsWithCheckout()
    {
        const int prNum = 77;
        var (context, _) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);

        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network unreachable"));

        // Checkout succeeds after fail-open
        _repoProvider.Setup(r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.MergeFromBaseAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult { Success = true, HasConflicts = false, ForceResolved = false, ConflictFiles = [] });

        var step = new CreateBranchStep();
        // Should not throw; should proceed to checkout
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Checkout was attempted despite the state query failing
        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "must attempt checkout when PR state query fails (fail-open)");
    }

    // ── PR number plumbing ─────────────────────────────────────────────────────

    /// <summary>
    /// Regression: review runs were dispatched with <c>LinkedPullRequest.Number = 0</c>, so the guard
    /// queried PR #0, got a 404 and failed open. The state check must use the linked PR's real number.
    /// </summary>
    [Fact]
    public async Task ReviewRun_QueriesPrStateWithTheLinkedPrNumber()
    {
        const int prNum = 3363;
        var (context, _) = BuildContextWithLinkedPr(prNum, PipelineRunType.Review);
        SetupOpenPrCheckout(prNum, new MergeResult { Success = true, HasConflicts = false, ConflictFiles = [] });

        await new CreateBranchStep().ExecuteAsync(context, CancellationToken.None);

        _repoProvider.Verify(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(
            r => r.GetPullRequestStateAsync(It.Is<int>(n => n != prNum), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A linked PR without a number is a dispatch bug: the step must not query PR #0, must log a
    /// warning (not debug) so the skipped guard is visible, and must still proceed with checkout.
    /// </summary>
    [Fact]
    public async Task WhenLinkedPrHasNoNumber_SkipsStateCheck_LogsWarning_AndProceedsWithCheckout()
    {
        var (context, _) = BuildContextWithLinkedPr(3363, PipelineRunType.Review, linkedPrNumber: 0);
        _repoProvider.Setup(r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await new CreateBranchStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _repoProvider.Verify(
            r => r.GetPullRequestStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "querying PR #0 can only 404");
        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _logSink.Events.Should().ContainSingle(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("no PR number"));
    }

    // ── Open PR passes through ─────────────────────────────────────────────────

    /// <summary>
    /// When PR is open, CreateBranchStep must proceed normally with checkout.
    /// </summary>
    [Fact]
    public async Task WhenPrIsOpen_ProceedsWithCheckout()
    {
        const int prNum = 11;
        var (context, _) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);

        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Open);

        _repoProvider.Setup(r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.MergeFromBaseAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult { Success = true, HasConflicts = false, ForceResolved = false, ConflictFiles = [] });

        var step = new CreateBranchStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "must checkout the branch when PR is open");
    }

    // ── Rework context ─────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #3093: when the rebase onto main drops the branch's changes to conflicting files, the
    /// step writes what was dropped to <c>.agent/rework-context.md</c> for the rework agent.
    /// </summary>
    [Fact]
    public async Task WhenRebaseForceResolves_WritesReworkContextFile()
    {
        const int prNum = 12;
        var (context, run) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);
        SetupOpenPrCheckout(prNum, new MergeResult
        {
            Success = true,
            HasConflicts = true,
            ForceResolved = true,
            ConflictFiles = ["src/A.cs"],
            ForceResolvedContext =
            [
                new ForceResolvedFileContext { Path = "src/A.cs", BranchChange = "+branch\n", BaseChange = "+main\n" }
            ]
        });

        try
        {
            await new CreateBranchStep().ExecuteAsync(context, CancellationToken.None);

            var filePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.ReworkContextFilePath);
            File.Exists(filePath).Should().BeTrue();
            (await File.ReadAllTextAsync(filePath)).Should().Contain("`src/A.cs`").And.Contain("+branch");
            run.MergeForceResolved.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task WhenRebaseHasNoConflicts_WritesNoReworkContextFile()
    {
        const int prNum = 13;
        var (context, run) = BuildContextWithLinkedPr(prNum, PipelineRunType.Implementation);
        SetupOpenPrCheckout(prNum, new MergeResult { Success = true, HasConflicts = false, ConflictFiles = [] });

        await new CreateBranchStep().ExecuteAsync(context, CancellationToken.None);

        File.Exists(Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.ReworkContextFilePath)).Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SetupOpenPrCheckout(int prNumber, MergeResult mergeResult)
    {
        _repoProvider.Setup(r => r.GetPullRequestStateAsync(prNumber, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Open);
        _repoProvider.Setup(r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.MergeFromBaseAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mergeResult);
    }

    private (PipelineStepContext context, PipelineRun run) BuildContextWithLinkedPr(
        int prNumber, PipelineRunType runType, int? linkedPrNumber = null)
    {
        var run = new PipelineRun
        {
            RunId = $"create-branch-test-{prNumber}",
            IssueIdentifier = prNumber.ToString(),
            IssueTitle = $"Test PR #{prNumber}",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"create-branch-{Guid.NewGuid():N}"),
            BranchName = $"feature/auto-{prNumber}-test",
            RunType = runType,
            LinkedPullRequest = new LinkedPullRequest
            {
                Number = linkedPrNumber ?? prNumber,
                BranchName = $"feature/auto-{prNumber}-test",
                Url = $"https://github.com/org/repo/pull/{prNumber}",
                IsDraft = false
            }
        };

        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp"
            },
            RepoProvider = _repoProvider.Object,
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = Mock.Of<IQualityGateValidator>()
        };

        return (context, run);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
