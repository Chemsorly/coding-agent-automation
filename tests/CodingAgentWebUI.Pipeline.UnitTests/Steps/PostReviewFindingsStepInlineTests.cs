using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="PostReviewFindingsStep"/> inline comment orchestration.
/// Tests inline comment posting, body-only fallback, retry loop, dismiss/collapse,
/// degradation tracking, and non-fatal behavior.
/// Feature: 026-inline-review-comments, Requirements: Req 6, 10, 11, 12, 15. Properties: P8.
/// </summary>
public class PostReviewFindingsStepInlineTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<CancellationTokenSource> _tokenSources = new();

    private PipelineStepContext BuildContext(
        PipelineRun run,
        bool supportsInline = false,
        bool inlineEnabled = false,
        FindingSeverity severityThreshold = FindingSeverity.Warning,
        int maxInlineComments = 15,
        int maxRetries = 1,
        IReadOnlyList<ReviewerConfiguration>? resolvedReviewerConfigs = null)
    {
        _repoProvider.Setup(r => r.SupportsInlineReviewComments).Returns(supportsInline);

        var cts = new CancellationTokenSource();
        _tokenSources.Add(cts);

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration
            {
                InlineComments = new InlineCommentSettings
                {
                    Enabled = inlineEnabled,
                    SeverityThreshold = severityThreshold,
                    MaxInlineComments = maxInlineComments,
                    MaxRetries = maxRetries,
                    OrderBySeverity = true
                }
            }
        };

        var context = new PipelineStepContext
        {
            Run = run,
            Config = config,
            RepoProvider = _repoProvider.Object,
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = cts,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = _agentExecution.Object,
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ResolvedReviewerConfigs = resolvedReviewerConfigs
        };

        return context;
    }

    private static PipelineRun BuildRun(string prNumber = "42", string? workspacePath = "/workspace")
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = prNumber,
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            WorkspacePath = workspacePath,
            CodeReviewAgentsRun = new[] { "SecurityBot" }
        };
        return run;
    }

    // ─── (a) Inline comments posted when enabled + supported ───────────────────

    [Fact]
    public async Task ExecuteAsync_InlineEnabled_ProviderSupportsInline_SubmitsReviewSubmissionWithComments()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify the ReviewSubmission overload was called with non-empty Comments
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42,
            It.Is<ReviewSubmission>(s => s.Comments.Count > 0 && s.CommitId == "abc123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── (b) Body-only when disabled ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InlineDisabled_SubmitsBodyOnly()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var context = BuildContext(run, supportsInline: false, inlineEnabled: false);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify body-only overload was called with RequestChanges (criticals present)
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── (c) Body-only when provider doesn't support inline ───────────────────

    [Fact]
    public async Task ExecuteAsync_InlineEnabled_ProviderDoesNotSupportInline_SubmitsBodyOnly()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        var context = BuildContext(run, supportsInline: false, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify body-only overload was called with RequestChanges (criticals present)
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── (d) Retry loop invokes ExecuteFollowUpAsync per-agent ────────────────

    [Fact]
    public async Task ExecuteAsync_AgentHasMarkersButNoLocation_InvokesExecuteFollowUpAsync()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        // Agent output has severity markers but NO file:line reference
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] — General security concern without file reference";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reviewerConfig = new ReviewerConfiguration
        {
            DisplayName = "Security Review",
            Agents = new[] { new ReviewAgent { Name = "SecurityBot", Prompt = "Review for security" } }
        };

        // Set up the follow-up to return structured output
        _agentExecution.Setup(a => a.ExecuteFollowUpAsync(
            It.IsAny<AgentPhaseContext>(),
            It.Is<ReviewerConfiguration>(rc => rc.DisplayName == "Security Review"),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync("[CRITICAL] src/Service.cs:42 — SQL injection vulnerability");

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true,
            resolvedReviewerConfigs: new[] { reviewerConfig });
        // Set Issue and ParsedIssue so BuildAgentPhaseContext works
        context.Issue = new IssueDetail
        {
            Identifier = "42",
            Title = "Test PR",
            Description = "Test body",
            Labels = Array.Empty<string>()
        };
        context.ParsedIssue = new ParsedIssue
        {
            RequirementsSection = "Test requirements",
            AcceptanceCriteria = Array.Empty<string>()
        };

        var step = new PostReviewFindingsStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify ExecuteFollowUpAsync was called for the agent
        _agentExecution.Verify(a => a.ExecuteFollowUpAsync(
            It.IsAny<AgentPhaseContext>(),
            It.Is<ReviewerConfiguration>(rc => rc.Agents.Any(a => a.Name == "SecurityBot")),
            It.Is<string>(prompt => prompt.Contains("General security concern")),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ─── (e) 422 triggers body-only retry ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SubmissionThrows_RetriesBodyOnly()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // First call (ReviewSubmission overload) throws, second call (body-only) succeeds
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("422 Validation Failed"));
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        // Verify body-only fallback was called with RequestChanges after the ReviewSubmission failure
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── (f) Degradation tracked on PipelineRun ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SubmissionFails_SetsDegradationOnPipelineRun()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ReviewSubmission overload throws
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("422 Validation Failed"));
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        run.InlineCommentsDegraded.Should().BeTrue();
        run.InlineCommentsDegradedReason.Should().NotBeNullOrEmpty();
        run.InlineCommentsDegradedReason.Should().Contain("422 Validation Failed");
    }

    // ─── (g) Dismiss called before submit ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SupportsInline_DismissCalledBeforeSubmit()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        var callOrder = new List<string>();

        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("dismiss"))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("submit"))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        callOrder.Should().ContainInOrder("dismiss", "submit");
        _repoProvider.Verify(r => r.DismissPreviousReviewAsync(
            42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── (h) Collapse fallback for non-inline providers ───────────────────────

    [Fact]
    public async Task ExecuteAsync_ProviderDoesNotSupportInline_CollapsesExistingReviews()
    {
        var run = BuildRun();
        run.CodeReviewAgentsRun = new[] { "StyleBot" };
        run.CodeReviewWarningCount = 1;
        run.CodeReviewAgentFindings["StyleBot"] = "[WARNING] Naming issues";

        // Simulate existing review comment found on first call, then null (collapsed)
        var callCount = 0;
        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(
            42, ReviewFindingsFormatter.Marker, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref callCount) == 1 ? 99999L : null);
        _repoProvider.Setup(r => r.UpdateReviewCommentAsync(
            42, 99999L, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: false, inlineEnabled: false);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify collapse was called (FindExistingReviewCommentAsync + UpdateReviewCommentAsync)
        _repoProvider.Verify(r => r.FindExistingReviewCommentAsync(
            42, ReviewFindingsFormatter.Marker, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _repoProvider.Verify(r => r.UpdateReviewCommentAsync(
            42, 99999L, It.Is<string>(body => body.Contains("Superseded")), It.IsAny<CancellationToken>()), Times.Once);
        // Verify DismissPreviousReviewAsync was NOT called (non-inline provider)
        _repoProvider.Verify(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── (i) Non-fatal on all failures ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllSubmissionsFail_ReturnsContinue()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Both ReviewSubmission and body-only overloads throw
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("422 Validation Failed"));
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        // Should NOT throw — non-fatal on all failures
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue,
            "Step should return Continue even when all submissions fail (non-fatal)");
    }

    // ─── (j) "Findings by Location" section appended for non-inline providers ─

    [Fact]
    public async Task ExecuteAsync_NonInlineProvider_InlineEnabled_BodyOnlyWithFindingsByLocation()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewWarningCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] =
            "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability\n[WARNING] src/Controller.cs:10 — Missing validation";

        _repoProvider.Setup(r => r.FindExistingReviewCommentAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long?)null);

        string? postedBody = null;
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()))
            .Callback<int, string, PullRequestReviewType, CancellationToken>((_, body, _, _) => postedBody = body)
            .Returns(Task.CompletedTask);

        // Provider doesn't support inline, but inline is enabled — should append "Findings by Location"
        var context = BuildContext(run, supportsInline: false, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // The body should be posted with RequestChanges (criticals/warnings present)
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<string>(), PullRequestReviewType.RequestChanges, It.IsAny<CancellationToken>()), Times.Once);

        // Verify body-only was called (not ReviewSubmission overload)
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Additional: InlineCommentsPosted tracked on success ──────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessfulInlineSubmission_TracksInlineCommentsPosted()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 2;
        run.CodeReviewAgentFindings["SecurityBot"] =
            "[CRITICAL] src/Service.cs:42 — SQL injection\n[CRITICAL] src/Auth.cs:10 — Missing auth check";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        run.InlineCommentsPosted.Should().BeGreaterThan(0);
        run.InlineCommentsDegraded.Should().BeFalse();
    }

    // ─── (k) GetHeadCommitShaAsync failure still submits with null CommitId ───

    [Fact]
    public async Task ExecuteAsync_GetHeadCommitShaFails_SubmitsWithNullCommitId()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] src/Service.cs:42 — SQL injection vulnerability";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Git HEAD not found"));
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run, supportsInline: true, inlineEnabled: true);
        var step = new PostReviewFindingsStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        // Verify submission still happens with CommitId = null
        _repoProvider.Verify(r => r.SubmitPullRequestReviewAsync(
            42,
            It.Is<ReviewSubmission>(s => s.CommitId == null && s.Comments.Count > 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── (l) ResolvedReviewerConfigs null skips retry ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_ResolvedReviewerConfigsNull_SkipsRetry_SubmitsBodyOnly()
    {
        var run = BuildRun();
        run.CodeReviewCriticalCount = 1;
        // Agent output has severity markers but NO file:line reference — candidate for retry
        run.CodeReviewAgentFindings["SecurityBot"] = "[CRITICAL] — General security concern without file reference";

        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _repoProvider.Setup(r => r.DismissPreviousReviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.SubmitPullRequestReviewAsync(
            42, It.IsAny<ReviewSubmission>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // ResolvedReviewerConfigs = null — retry should be skipped
        var context = BuildContext(run, supportsInline: true, inlineEnabled: true, resolvedReviewerConfigs: null);
        var step = new PostReviewFindingsStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Verify ExecuteFollowUpAsync was NOT called (retry skipped because no reviewer config found)
        _agentExecution.Verify(a => a.ExecuteFollowUpAsync(
            It.IsAny<AgentPhaseContext>(),
            It.IsAny<ReviewerConfiguration>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        foreach (var cts in _tokenSources)
            cts.Dispose();
        (_logger as IDisposable)?.Dispose();
    }
}
