using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentPhaseExecutor.ExecuteFollowUpAsync"/>.
/// Covers the follow-up reviewer dispatch, guard checks, error handling, and
/// the empty-prompt fast path.
/// </summary>
public class AgentPhaseExecutorFollowUpTests : IDisposable
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly AgentPhaseExecutor _executor;
    private readonly string _workspacePath;

    public AgentPhaseExecutorFollowUpTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _workspacePath = Path.Combine(Path.GetTempPath(), $"test-followup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _run = new PipelineRun
        {
            RunId = "test-run-followup",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = _workspacePath
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _executor = new AgentPhaseExecutor(_mockLogger.Object);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockAgent.SetupGet(a => a.SupportsParallelExecution).Returns(false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    private AgentPhaseContext BuildContext() => new()
    {
        Run = _run,
        Config = _config,
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        OrchestratorCts = null,
        Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Desc", Labels = ["bug"] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "req", AcceptanceCriteria = ["AC1"] }
    };

    private static ReviewerConfiguration BuildReviewerConfig(string name = "TestReviewer") => new()
    {
        DisplayName = name,
        Agents = [new ReviewAgent { Name = name, Prompt = $"Review as {name}" }]
    };

    // ── Guard checks ──────────────────────────────────────────────────────
    // Note: ArgumentNullException.ThrowIfNull is called inside a try/catch(Exception)
    // block, so null arguments are caught and return empty string rather than propagating.

    [Fact]
    public async Task ExecuteFollowUpAsync_NullContext_ReturnsEmptyString()
    {
        // The null check is inside try/catch(Exception) — the exception is swallowed
        var result = await _executor.ExecuteFollowUpAsync(
            null!, BuildReviewerConfig(), "prompt", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteFollowUpAsync_NullReviewerConfig_ReturnsEmptyString()
    {
        // Same: null check is inside try/catch(Exception), so exception is swallowed
        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), null!, "prompt", CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── Empty/null prompt fast path ───────────────────────────────────────

    [Fact]
    public async Task ExecuteFollowUpAsync_EmptyPrompt_ReturnsEmptyString()
    {
        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(), string.Empty, CancellationToken.None);

        result.Should().BeEmpty();
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteFollowUpAsync_NullPrompt_ReturnsEmptyString()
    {
        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(), null!, CancellationToken.None);

        result.Should().BeEmpty();
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    // ── Normal execution path ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteFollowUpAsync_WithPrompt_InvokesAgentAndReturnsResponse()
    {
        var responseText = "The code looks good after the fix.";
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [responseText] });

        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig("SecurityReviewer"),
            "Please verify the fix applied correctly.",
            CancellationToken.None);

        result.Should().Be(responseText);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteFollowUpAsync_AgentReturnsNoOutputLines_ReturnsEmptyString()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(),
            "Verify the change.",
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteFollowUpAsync_AgentReturnsMultipleLines_JoinsWithNewLine()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["Line one", "Line two", "Line three"] });

        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(),
            "Verify.",
            CancellationToken.None);

        result.Should().Contain("Line one");
        result.Should().Contain("Line two");
        result.Should().Contain("Line three");
    }

    // ── Exception handling ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteFollowUpAsync_AgentThrows_ReturnsEmptyStringInsteadOfPropagating()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("Agent crashed"));

        var result = await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(),
            "Verify after fix.",
            CancellationToken.None);

        // Exception should be swallowed and empty string returned
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteFollowUpAsync_Cancelled_PropagatesCancellationException()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await _executor.ExecuteFollowUpAsync(
            BuildContext(), BuildReviewerConfig(),
            "Verify.",
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
