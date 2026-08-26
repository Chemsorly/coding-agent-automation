using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="FetchIssueStep"/>.
///
/// This step is the context entry-point for all downstream pipeline steps — it populates
/// <c>context.Issue</c>, <c>context.ParsedIssue</c>, <c>context.IssueComments</c>,
/// <c>context.Run.IssueTitle</c>, and <c>context.Run.IssueLabels</c>. Every downstream step
/// test uses empty stubs because this step currently has no dedicated tests.
///
/// Coverage targets:
/// - Null IssueProvider → throws InvalidOperationException (not silently swallowed)
/// - GetIssueAsync failure → StepResult.Stop, FailRunAsync called
/// - Empty title or description → StepResult.Stop, FailRunAsync called
/// - ListCommentsAsync failure → StepResult.Continue (non-fatal), empty comments
/// - Happy path (no comments) → context fully populated
/// - Happy path (with comments) → comments forwarded
/// - Run metadata (Title, Labels) forwarded from IssueDetail
/// - Images extracted from issue body
/// - IssueDetail reconstruction preserves all five init properties
/// - Cancellation propagates (OperationCanceledException not swallowed)
/// </summary>
public sealed class FetchIssueStepTests
{
    private static readonly Serilog.ILogger _logger =
        new Serilog.LoggerConfiguration().CreateLogger();

    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IIssueProvider> _issueProvider = new();

    public FetchIssueStepTests()
    {
        // FailRunAsync calls these Callbacks methods — set up default stubs
        _callbacks
            .Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks
            .Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _callbacks
            .Setup(c => c.EmitOutputLine(It.IsAny<string>()));

        _issueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static FetchIssueStep CreateStep() =>
        new(new IssueDescriptionParser(), new IssueImageExtractor());

    private PipelineStepContext BuildContext(IIssueProvider? provider = null) =>
        new()
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Pending",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = PipelineRunType.Implementation
            },
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            IssueProvider = provider ?? _issueProvider.Object
        };

    private static IssueDetail MakeIssue(
        string title = "Fix the bug",
        string description = "The system crashes on startup.",
        string identifier = "42",
        IReadOnlyList<string>? labels = null) =>
        new()
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = labels ?? new[] { "bug" }
        };

    // ── Guard: null IssueProvider ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullIssueProvider_ThrowsInvalidOperationException()
    {
        var step = CreateStep();
        var context = BuildContext(provider: null);
        // Override IssueProvider to null (requires constructing context without it)
        var contextWithNullProvider = new PipelineStepContext
        {
            Run = context.Run,
            Config = context.Config,
            RepoProvider = context.RepoProvider,
            AgentProvider = context.AgentProvider,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = context.ConfigStore,
            Callbacks = _callbacks.Object,
            IssueOps = context.IssueOps,
            AgentExecution = context.AgentExecution,
            QualityGates = context.QualityGates,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            IssueProvider = null
        };

        var act = () => step.ExecuteAsync(contextWithNullProvider, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            because: "FetchIssueStep requires an IssueProvider — null means a wiring bug that must surface immediately");
    }

    // ── GetIssueAsync failure ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_GetIssueAsyncThrows_ReturnsStop_AndCallsFailRunAsync()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop, because: "fetch failure must stop the pipeline");
        context.Run.FailureReason.Should().NotBeNullOrEmpty(
            because: "FailRunAsync must have been called and set the failure reason");
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_GetIssueAsyncThrows_FailureReasonContainsExceptionMessage()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limit exceeded"));

        var step = CreateStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.FailureReason.Should().Contain("rate limit exceeded",
            because: "the failure message must include the original exception message for diagnosability");
    }

    // ── Empty title / description ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyTitle_ReturnsStop_AndCallsFailRunAsync()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeIssue(title: ""));

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDescription_ReturnsStop_AndCallsFailRunAsync()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeIssue(description: ""));

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceDescription_ReturnsStop()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeIssue(description: "   "));

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    // ── ListCommentsAsync failure (non-fatal) ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CommentFetchFails_StillReturnsContinue_WithEmptyComments()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeIssue());
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue,
            because: "comment fetch failure must not stop the pipeline — issue data is still usable");
        context.IssueComments.Should().NotBeNull().And.BeEmpty(
            because: "failed comment fetch leaves an empty list, not null");
    }

    // ── Happy path — context fully populated ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HappyPath_NoComments_ReturnsContine_PopulatesContext()
    {
        var issue = MakeIssue();
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Issue.Should().NotBeNull(because: "step must populate Issue");
        context.ParsedIssue.Should().NotBeNull(because: "step must populate ParsedIssue");
        context.IssueComments.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WithComments_CommentsForwarded()
    {
        var issue = MakeIssue();
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "alice", Body = "LGTM", CreatedAt = DateTime.UtcNow },
            new() { Id = "2", Author = "bob", Body = "Needs work", CreatedAt = DateTime.UtcNow }
        };
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);

        var step = CreateStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.IssueComments.Should().HaveCount(2, because: "both comments must be forwarded to context");
    }

    // ── Run metadata forwarding ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HappyPath_SetsRunIssueTitleAndLabels()
    {
        var labels = new[] { "enhancement", "frontend" };
        var issue = MakeIssue(title: "Add dark mode", labels: labels);
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        var step = CreateStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.IssueTitle.Should().Be("Add dark mode",
            because: "Run.IssueTitle must be updated from the fetched issue");
        context.Run.IssueLabels.Should().BeEquivalentTo(labels,
            because: "Run.IssueLabels must be updated from the fetched issue");
    }

    // ── IssueDetail reconstruction preserves all init properties ─────────────

    [Fact]
    public async Task ExecuteAsync_HappyPath_ContextIssue_PreservesAllProperties()
    {
        var issue = new IssueDetail
        {
            Identifier = "42",
            Title = "Fix startup crash",
            Description = "App crashes on first launch due to missing config.",
            Labels = new[] { "bug", "critical" }
        };
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        var step = CreateStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Issue.Should().NotBeNull();
        context.Issue!.Identifier.Should().Be(issue.Identifier,
            because: "Identifier must be preserved in the reconstructed IssueDetail");
        context.Issue.Title.Should().Be(issue.Title,
            because: "Title must be preserved");
        context.Issue.Description.Should().Be(issue.Description,
            because: "Description must be preserved");
        context.Issue.Labels.Should().BeEquivalentTo(issue.Labels,
            because: "Labels must be preserved");
    }

    // ── Image extraction ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_IssueBodyWithImageMarkdown_ExtractsImages()
    {
        // Markdown image syntax that IssueImageExtractor should recognise
        const string descriptionWithImage =
            "## Context\n\n![Screenshot](https://example.com/screenshot.png)\n\nFix the layout.";

        var issue = MakeIssue(description: descriptionWithImage);
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(issue);
        _issueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueComment>());

        var step = CreateStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Issue.Should().NotBeNull();
        context.Issue!.Images.Should().NotBeEmpty(
            because: "the step must extract images from the issue body and populate Images on the reconstructed IssueDetail");
    }

    // ── Cancellation propagates ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancellationDuringFetch_ThrowsOperationCanceledException()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var step = CreateStep();
        var context = BuildContext();

        var act = () => step.ExecuteAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "OperationCanceledException must propagate — it must never be swallowed by the catch block");
    }
}
