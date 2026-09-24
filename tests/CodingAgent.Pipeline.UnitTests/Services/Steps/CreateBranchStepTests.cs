using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

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
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<string> _outputLines = [];
    private readonly List<PipelineStep> _transitions = [];

    public CreateBranchStepTests()
    {
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "must attempt checkout when PR state query fails (fail-open)");
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
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.MergeFromBaseAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeResult { Success = true, HasConflicts = false, ForceResolved = false, ConflictFiles = [] });

        var step = new CreateBranchStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        _repoProvider.Verify(
            r => r.CheckoutRemoteBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "must checkout the branch when PR is open");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private (PipelineStepContext context, PipelineRun run) BuildContextWithLinkedPr(
        int prNumber, PipelineRunType runType)
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
                Number = prNumber,
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
}
