using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Isolated unit tests for <see cref="AgentPhaseExecutor.ExecuteCodeReviewAsync"/>.
/// Tests multi-reviewer loop, findings aggregation, fix prompt dispatch, and exception handling.
/// </summary>
public class AgentPhaseExecutorCodeReviewTests : IDisposable
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly AgentPhaseExecutor _executor;
    private readonly string _workspacePath;

    public AgentPhaseExecutorCodeReviewTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _workspacePath = Path.Combine(Path.GetTempPath(), $"test-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _run = new PipelineRun
        {
            RunId = "test-run-review",
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
            StallWarningInterval = TimeSpan.FromHours(1),
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 2,
                FixPrompt = "Fix the critical issues",
                ReviewIsolation = ReviewIsolation.Isolated
            }
        };

        _executor = new AgentPhaseExecutor(_mockLogger.Object);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockAgent.SetupGet(a => a.SupportsParallelExecution).Returns(false);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    [Fact]
    public async Task CodeReview_MaxIterationsZero_EarlyReturn()
    {
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 0 } };
        var context = BuildContext(config);

        await _executor.ExecuteCodeReviewAsync(context, CancellationToken.None, CreateReviewers("Agent1"));

        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task CodeReview_NoResolvedReviewers_EarlyReturn()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None, resolvedReviewerConfigs: null);

        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task CodeReview_EmptyResolvedReviewers_EarlyReturn()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None,
            resolvedReviewerConfigs: Array.Empty<ReviewerConfiguration>());

        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task CodeReview_SingleReviewer_FindingsParsedFromFile()
    {
        SetupAgentWritingFindings("correctness", "[CRITICAL] Bug found\n[WARNING] Style issue");
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        _run.CodeReviewCriticalCount.Should().Be(1);
        _run.CodeReviewWarningCount.Should().Be(1);
    }

    [Fact]
    public async Task CodeReview_MultipleReviewers_FindingsAggregated()
    {
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // First two calls are iteration 1 agents, next two would be iteration 2
                var agentName = callCount % 2 == 1 ? "correctness" : "security";
                var findings = callCount % 2 == 1 ? "[CRITICAL] Bug" : "[WARNING] Issue\n[SUGGESTION] Hint";
                WriteFindingsFile(agentName, findings);
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var reviewers = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Code Quality",
                Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review" }, new ReviewAgent { Name = "Security", Prompt = "Review security" } }
            }
        };

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, reviewers);

        _run.CodeReviewCriticalCount.Should().Be(1);
        _run.CodeReviewWarningCount.Should().Be(1);
        _run.CodeReviewSuggestionCount.Should().Be(1);
    }

    [Fact]
    public async Task CodeReview_CriticalFindings_FixPromptDispatched()
    {
        SetupAgentWritingFindings("correctness", "[CRITICAL] Bug found");

        // The fix prompt call is the second ExecuteAsync invocation
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                if (callCount == 1)
                    WriteFindingsFile("correctness", "[CRITICAL] Bug found");
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = "Fix it" } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Review agent + fix agent = 2 calls
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CodeReview_NoCriticalFindings_FixPromptSkipped()
    {
        SetupAgentWritingFindings("correctness", "[WARNING] Minor issue");
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = "Fix it" } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Review agent + fix prompt for warnings (no re-review since no criticals → early exit)
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CodeReview_SequentialException_BreaksIterationLoop()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"));

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 3, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Exception breaks the loop — only 1 attempt (the first iteration fails)
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
        _run.CodeReviewIterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task CodeReview_ParallelMode_IsolatesAgentFailures()
    {
        _mockAgent.SetupGet(a => a.SupportsParallelExecution).Returns(true);

        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                    throw new InvalidOperationException("first agent crashed");
                WriteFindingsFile("security", "[WARNING] Finding");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        var reviewers = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Quality",
                Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review" }, new ReviewAgent { Name = "Security", Prompt = "Review" } }
            }
        };

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null, ReviewIsolation = ReviewIsolation.Isolated } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, reviewers);

        // Second agent's findings still counted despite first agent failing
        _run.CodeReviewWarningCount.Should().Be(1);
        _run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task CodeReview_FindingsFileMissing_EmptyFindingsNotFailure()
    {
        // Agent runs but doesn't write a findings file
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        _run.CodeReviewCriticalCount.Should().Be(0);
        _run.CodeReviewWarningCount.Should().Be(0);
        _run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    private AgentPhaseContext BuildContext(PipelineConfiguration? config = null)
    {
        return new AgentPhaseContext
        {
            Run = _run,
            Config = config ?? _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = null,
            Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Test description", Labels = new[] { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "Test requirements", AcceptanceCriteria = new[] { "AC1", "AC2" } }
        };
    }

    private static IReadOnlyList<ReviewerConfiguration> CreateReviewers(params string[] agentNames)
    {
        return new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Test Reviewer",
                Agents = agentNames.Select(n => new ReviewAgent { Name = n, Prompt = $"Review as {n}" }).ToArray()
            }
        };
    }

    private void SetupAgentWritingFindings(string agentName, string findings)
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) => WriteFindingsFile(agentName, findings))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
    }

    private void WriteFindingsFile(string agentName, string content)
    {
        var relativePath = AgentWorkspacePaths.GetReviewFindingsFilePath(agentName);
        var fullPath = Path.Combine(_workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
