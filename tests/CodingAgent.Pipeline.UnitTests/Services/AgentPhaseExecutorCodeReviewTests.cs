using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace CodingAgent.Pipeline.UnitTests;

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
    private readonly TestMeterFactory _meterFactory = new();

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
            AcceptanceCriteriaEnabled = false,
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 2,
                FixPrompt = "Fix the critical issues"
            }
        };

        _executor = new AgentPhaseExecutor(_mockLogger.Object, _meterFactory);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockAgent.SetupGet(a => a.SupportsParallelExecution).Returns(false);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
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
    // TODO [WARNING]: This test covers the null-configs path but only asserts that the agent is never
    // called — it does NOT assert the mandatory _logger.Warning signal introduced by issue #2228.
    // This makes it a weaker duplicate of WhenResolvedReviewerConfigsIsNull_LogsWarningAtWarnLevel below.
    // A future refactor removing the logger call would leave this test green while silently losing the
    // observable signal. Consider consolidating: either remove this test (WhenResolvedReviewerConfigsIsNull
    // is the authoritative coverage) or extend it with the Warning assertion. (Correctness + TestQuality review, issue #2228)
    public async Task CodeReview_NoResolvedReviewers_EarlyReturn()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None, resolvedReviewerConfigs: null);

        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    // TODO [WARNING]: This test covers the empty-list-configs path but only asserts that the agent is
    // never called — it does NOT assert the mandatory _logger.Warning signal introduced by issue #2228.
    // This makes it a weaker duplicate of WhenResolvedReviewerConfigsIsEmpty_LogsWarningAtWarnLevel below.
    // Consider consolidating with the Warning-asserting test below. (TestQuality review, issue #2228)
    public async Task CodeReview_EmptyResolvedReviewers_EarlyReturn()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None,
            resolvedReviewerConfigs: Array.Empty<ReviewerConfiguration>());

        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task WhenResolvedReviewerConfigsIsNull_LogsWarningAtWarnLevel()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None, resolvedReviewerConfigs: null);

        // TODO [WARNING]: The second argument uses It.IsAny<string>() which does not verify that the actual
        // run.RunId ("test-run-review") is passed — any string (including empty or null) would satisfy this
        // mock verification. Tighten to It.Is<string>(id => id == _run.RunId) to make the RunId assertion
        // meaningful and prevent a regression where the implementation passes the wrong field. (TestQuality review, issue #2228)
        _mockLogger.Verify(
            l => l.Warning(
                "Pipeline {RunId} no reviewer configurations matched — review phase skipped (no configs or all disabled). " +
                "To restore review, add or re-enable a reviewer configuration in Settings → Reviewers.",
                It.IsAny<string>()),
            Times.Once);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task WhenResolvedReviewerConfigsIsEmpty_LogsWarningAtWarnLevel()
    {
        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None,
            resolvedReviewerConfigs: Array.Empty<ReviewerConfiguration>());

        // TODO [WARNING]: Same weak assertion as above — It.IsAny<string>() for the RunId argument does not
        // verify the correct field is passed. Tighten to It.Is<string>(id => id == _run.RunId). (TestQuality review, issue #2228)
        _mockLogger.Verify(
            l => l.Warning(
                "Pipeline {RunId} no reviewer configurations matched — review phase skipped (no configs or all disabled). " +
                "To restore review, add or re-enable a reviewer configuration in Settings → Reviewers.",
                It.IsAny<string>()),
            Times.Once);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
    }

    [Fact]
    public async Task WhenResolvedReviewerConfigsHasEntries_DoesNotLogSkipWarning()
    {
        SetupAgentWritingFindings("correctness", "");
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        _mockLogger.Verify(
            l => l.Warning(
                "Pipeline {RunId} no reviewer configurations matched — review phase skipped (no configs or all disabled). " +
                "To restore review, add or re-enable a reviewer configuration in Settings → Reviewers.",
                It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenFlattenedAgentsIsEmpty_LogsWarningAtWarnLevel()
    {
        var configs = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Empty",
                Agents = Array.Empty<ReviewAgent>()
            }
        };

        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None,
            resolvedReviewerConfigs: configs);

        _mockLogger.Verify(
            l => l.Warning(
                "Pipeline {RunId} reviewer configurations matched but resolved to zero agents — review phase skipped. " +
                "Ensure each enabled ReviewerConfiguration has at least one agent defined.",
                It.Is<string>(id => id == _run.RunId)),
            Times.Once);
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenFlattenedAgentsIsEmpty_IncrementsReviewSkippedCounter()
    {
        var configs = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Empty",
                Agents = Array.Empty<ReviewAgent>()
            }
        };

        using var collector = new MetricCollector<long>(
            _meterFactory, PipelineTelemetry.SourceName, "pipeline.review.skipped");

        await _executor.ExecuteCodeReviewAsync(BuildContext(), CancellationToken.None,
            resolvedReviewerConfigs: configs);

        // TODO: Also assert telemetry tags emitted by PipelineTelemetry.BuildTags (run_type,
        // pipeline.project_id, pipeline.project_name). Currently, a regression that passes the
        // wrong PipelineRun or omits tags entirely would still satisfy this assertion because
        // MetricCollector records Value independently of Tags. The same gap exists in the
        // analogous empty-configs counter tests added in #2228. Tighten by checking e.g.:
        // collector.GetMeasurementSnapshot().Should().ContainSingle(
        //     m => m.Value == 1 && m.Tags.ToList().Any(t => t.Key == "run_type"));
        collector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
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

        // Review agent + fix agent + summary agent = 3 calls
        // TODO: Add a test that sets up the mock to return valid "## Change Summary\n...\n## Review Verdict\n..."
        // output for the summary agent call, and assert _run.CodeReviewChangeSummary/VerdictSummary are populated.
        // Currently, all mocks return empty OutputLines so the summary parser always returns (null, null) —
        // the happy path (agent → parse → field assignment) is never tested as an integrated flow.
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(3));
    }

    [Fact]
    // TODO: Add a multi-iteration test (maxIterations=2) for SendFixAndBreak early-exit path.
    // Current test uses maxIterations=1, making early-return indistinguishable from normal loop termination.
    // A test with maxIterations=2 where iteration 1 produces only warnings would validate the loop exits early.
    public async Task CodeReview_NoCriticalFindings_FixPromptSkipped()
    {
        SetupAgentWritingFindings("correctness", "[WARNING] Minor issue");
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = "Fix it" } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Review agent + fix prompt for warnings + summary agent = 3 calls
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(3));
    }

    [Fact]
    public async Task CodeReview_SequentialException_SingleAgent_IterationCompletesWithFailureResult()
    {
        // After the fix, ExecuteSingleReviewAgentSafeAsync catches the exception and returns Failure.
        // The iteration completes normally (no loop break) — the Failure result has no findings,
        // FixPrompt=null produces Skip (loop continues), so all MaxIterations run.
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"));

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 3, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // FixPrompt=null → Skip (loop continues all 3 iterations), each iteration: 1 review call.
        // Then 1 summary call = 4 total.
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(4));
        // All 3 iterations complete (Failure result is returned, not thrown).
        _run.CodeReviewIterationsCompleted.Should().Be(3);
        // No findings recorded for crashed agents.
        _run.CodeReviewCriticalCount.Should().Be(0);
        _run.CodeReviewWarningCount.Should().Be(0);
    }

    [Fact]
    public async Task WhenSequentialAgentCrashes_RemainingAgentsContinue()
    {
        // AgentA crashes; AgentB must still run and its findings must be counted.
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                    throw new InvalidOperationException("AgentA crashed");
                // n == 2: AgentB writes a warning finding
                // TODO: Use else-if (n == 2) guard to prevent WriteFindingsFile being called for n >= 3
                // (the summary agent call). Currently harmless because findings are consumed before the
                // summary call, but the control flow is misleading. Also: AgentWorkspacePaths.GetReviewFindingsFilePath
                // lowercases the name via ToLowerInvariant(), so "agentb" and "AgentB" resolve to the same
                // path — the portability concern is moot, but aligning the key with the agent Name ("AgentB")
                // would make the intent clearer and guard against future path-resolution changes.
                WriteFindingsFile("agentb", "[WARNING] AgentB found something");
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
                // n == 3: summary agent — no findings file needed
            });

        var reviewers = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Test Reviewer",
                Agents = new[]
                {
                    new ReviewAgent { Name = "AgentA", Prompt = "Review as A" },
                    new ReviewAgent { Name = "AgentB", Prompt = "Review as B" }
                }
            }
        };
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, reviewers);

        // Both review agents + 1 summary agent = 3 total calls.
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(3));
        // Both agents recorded as run (crashed agent is still registered).
        _run.CodeReviewAgentsRun.Should().Contain("AgentA");
        _run.CodeReviewAgentsRun.Should().Contain("AgentB");
        // AgentB's finding is counted despite AgentA's crash.
        _run.CodeReviewWarningCount.Should().Be(1);
        _run.CodeReviewCriticalCount.Should().Be(0);
        // Iteration completed normally.
        _run.CodeReviewIterationsCompleted.Should().Be(1);
        // AgentA's crash was logged as a Warning with an Exception argument.
        // The safe wrapper calls Warning<string,string,int>(ex, template, runId, agentName, iteration).
        // TODO: Tighten the logger assertion to verify the specific RunId and AgentName ("AgentA")
        // rather than using It.IsAny<string>() for all parameters — any Warning(Exception, string, *, *, *)
        // call satisfies the current assertion, including calls with wrong context values.
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenSequentialAgentCrashes_OCEPropagates()
    {
        // OperationCanceledException from an agent must not be swallowed by the per-agent safe wrapper —
        // it must propagate out of the sequential loop, stopping all remaining agents in the iteration.
        using var cts = new CancellationTokenSource();

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                cts.Cancel(); // cancel as AgentA runs
                throw new OperationCanceledException(cts.Token);
            });

        var reviewers = new[]
        {
            new ReviewerConfiguration
            {
                DisplayName = "Test Reviewer",
                Agents = new[]
                {
                    new ReviewAgent { Name = "AgentA", Prompt = "Review as A" },
                    new ReviewAgent { Name = "AgentB", Prompt = "Review as B" }
                }
            }
        };
        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

        // OCE propagates from the sequential loop. The outer loop catch handles it when OrchestratorCts
        // is null; the summary agent may also propagate OCE — either way, the loop was broken by the OCE.
        try
        {
            await _executor.ExecuteCodeReviewAsync(BuildContext(config), cts.Token, reviewers);
        }
        catch (OperationCanceledException)
        {
            // Expected: OCE may propagate through the summary agent path
        }

        // The key invariant: AgentB was never called — OCE from AgentA stops the sequential loop.
        _run.CodeReviewAgentsRun.Should().NotContain("AgentB");
        // TODO: Also assert _run.CodeReviewAgentsRun.Should().NotContain("AgentA") — agentsRun.Add for
        // AgentA is also skipped when OCE propagates (it comes after the awaited safe wrapper), so an empty
        // agentsRun would be a stronger proof that OCE propagated rather than AgentB being skipped for
        // some other reason.
        // Iteration was not completed — OCE broke the sequential loop before all agents finished
        _run.CodeReviewIterationsCompleted.Should().Be(0);
        // TODO: The try/catch(OperationCanceledException){} above silently swallows the OCE. If the
        // production code swallows OCE and returns normally, both assertions above still pass — the test
        // cannot distinguish "OCE propagated correctly" from "OCE was swallowed but AgentB was skipped
        // for another reason". Consider asserting that the awaited call threw OCE (e.g., using
        // FluentAssertions ThrowAsync) to make the propagation contract falsifiable.
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

        var config = _config with { CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null } };

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

    #region Acceptance Criteria Tests

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_NonCompliantOnIteration1_CompliantOnIteration2_ReportShowsCompliant()
    {
        // Arrange: AC non-compliant on first check, compliant on second check after fix
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // Call 1: review agent (iteration 1) — no findings
                // Call 2: AC agent (iteration 1) — writes non-compliant
                // Call 3: fix agent
                // Call 4: review agent (iteration 2) — no findings
                // Call 5: AC agent (iteration 2) — writes compliant
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "non_compliant", "reasoning": "Not implemented yet" },
                            { "criterion": "Tests pass", "status": "non_compliant", "reasoning": "No tests" }
                        ],
                        "summary": "0 of 2 criteria addressed."
                    }
                    """);
                }
                else if (callCount == 5)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "compliant", "evidence": "Implemented" },
                            { "criterion": "Tests pass", "status": "compliant", "evidence": "All green" }
                        ],
                        "summary": "2 of 2 criteria addressed."
                    }
                    """);
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 2, FixPrompt = "Fix the issues" }
        };

        // Act
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: final report shows all compliant
        _run.AcceptanceCriteriaReport.Should().NotBeNull();
        _run.AcceptanceCriteriaReport!.Criteria.Should().HaveCount(2);
        _run.AcceptanceCriteriaReport.Criteria.Should().AllSatisfy(c => c.Status.Should().Be(CriterionStatus.Compliant));

        // TODO: Add _run.CodeReviewIterationsCompleted.Should().Be(2) to distinguish 2 iterations ran vs report being stale-compliant from a single iteration
        // TODO: Add _run.CodeReviewCriticalCount.Should().Be(2) to verify non-compliant criteria were injected as CRITICAL on iteration 1

        // Assert: 5 agent calls total (review + AC + fix + review + AC) + 1 summary = 6
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(6));
    }

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_CompliantOnIteration1_SingleExecution_ReportIsCompliant()
    {
        // Arrange: review agent finds nothing, AC is compliant → single iteration, early exit
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // Call 1: review agent — no findings file written
                // Call 2: AC agent — writes compliant JSON
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "compliant", "evidence": "Done" },
                            { "criterion": "Tests pass", "status": "compliant", "evidence": "All green" }
                        ],
                        "summary": "2 of 2 criteria addressed."
                    }
                    """);
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 2, FixPrompt = "Fix the issues" }
        };

        // Act
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: report shows all compliant
        _run.AcceptanceCriteriaReport.Should().NotBeNull();
        _run.AcceptanceCriteriaReport!.Criteria.Should().HaveCount(2);
        _run.AcceptanceCriteriaReport.Criteria.Should().AllSatisfy(c => c.Status.Should().Be(CriterionStatus.Compliant));

        // Assert: only 2 calls (review + AC) + 1 summary = 3, loop exits after single iteration (no findings)
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(3));
        _run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_NonCompliant_InjectsCriticalFindings()
    {
        // Arrange: AC writes 2 non-compliant criteria → injected as CRITICAL → fix dispatched
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // Call 1: review agent — no findings
                // Call 2: AC agent — writes non-compliant
                // Call 3: fix agent
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "non_compliant", "reasoning": "Not done" },
                            { "criterion": "Tests pass", "status": "non_compliant", "reasoning": "No tests" }
                        ],
                        "summary": "0 of 2 criteria addressed."
                    }
                    """);
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = "Fix the issues" }
        };

        // Act
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: 2 AC criticals counted
        _run.CodeReviewCriticalCount.Should().Be(2);

        // TODO: Add assertion _run.AcceptanceCriteriaReport.Should().NotBeNull() to verify report is stored alongside CRITICAL injection

        // Assert: fix prompt dispatched (review + AC + fix + summary = 4 calls)
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(4));
    }

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_TokenUsageAccumulated()
    {
        // Arrange: 2 iterations with AC on each → token usage from all 5 calls accumulated
        var callCount = 0;
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "non_compliant", "reasoning": "Not done" }
                        ],
                        "summary": "0 of 1 criteria addressed."
                    }
                    """);
                }
                else if (callCount == 5)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "compliant", "evidence": "Done" }
                        ],
                        "summary": "1 of 1 criteria addressed."
                    }
                    """);
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>(), Usage = usage });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 2, FixPrompt = "Fix the issues" }
        };

        // Act
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: 5 calls × 150 tokens each + 1 summary call = 6 × 150 = 900 total
        _run.TotalTokens.Should().Be(900);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(6));
    }

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_ParseFailure_DoesNotCrash()
    {
        // Arrange: AC agent succeeds but writes invalid JSON
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // Call 1: review agent — no findings
                // Call 2: AC agent — writes invalid JSON
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("not valid json {{{");
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 1, FixPrompt = null }
        };

        // Act — should not throw
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: report is null (parse failed gracefully), loop completed
        _run.AcceptanceCriteriaReport.Should().BeNull();
        _run.CodeReviewIterationsCompleted.Should().Be(1);
    }

    // TODO: Add boundary test: MaxIterations=2, AC non-compliant on both iterations → run.AcceptanceCriteriaReport reflects final non-compliant state and PR body correctly shows ❌

    [Fact]
    public async Task CodeReview_AcceptanceCriteria_ParseFailurePreservesExistingReport()
    {
        // Arrange: iteration 1 writes valid non-compliant JSON, iteration 2 writes invalid JSON.
        // The fix (null-coalescing guard) ensures the valid report from iteration 1 is preserved.
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                callCount++;
                // Call 1: review agent (iteration 1) — no findings
                // Call 2: AC agent (iteration 1) — writes non-compliant JSON
                // Call 3: fix agent (iteration 1) — CRITICAL from non-compliant AC
                // Call 4: review agent (iteration 2) — no findings
                // Call 5: AC agent (iteration 2) — writes invalid JSON (parse returns null)
                // Call 6: fix agent (iteration 2) — CRITICAL re-injected from preserved stale report
                // Call 7: summary agent
                if (callCount == 2)
                {
                    WriteAcceptanceCriteriaJson("""
                    {
                        "criteria": [
                            { "criterion": "Feature works", "status": "non_compliant", "reasoning": "Not implemented yet" },
                            { "criterion": "Tests pass", "status": "non_compliant", "reasoning": "No tests" }
                        ],
                        "summary": "0 of 2 criteria addressed."
                    }
                    """);
                }
                else if (callCount == 5)
                {
                    WriteAcceptanceCriteriaJson("not valid json {{{");
                }
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var config = _config with
        {
            AcceptanceCriteriaEnabled = true,
            CodeReview = new CodeReviewConfiguration { MaxIterations = 2, FixPrompt = "Fix the issues" }
        };

        // Act
        await _executor.ExecuteCodeReviewAsync(BuildContext(config), CancellationToken.None, CreateReviewers("Correctness"));

        // Assert: report from iteration 1 is preserved (not overwritten to null by iteration 2's parse failure)
        _run.AcceptanceCriteriaReport.Should().NotBeNull();
        _run.AcceptanceCriteriaReport!.Criteria.Should().HaveCount(2);
        _run.AcceptanceCriteriaReport.Criteria.Should().AllSatisfy(c => c.Status.Should().Be(CriterionStatus.NonCompliant));
        _run.AcceptanceCriteriaReport.Summary.Should().Be("0 of 2 criteria addressed.");

        // Assert: 7 calls total (review + AC + fix) × 2 iterations + summary
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(7));
        _run.CodeReviewIterationsCompleted.Should().Be(2);
    }

    private void WriteAcceptanceCriteriaJson(string json)
    {
        var fullPath = Path.Combine(_workspacePath, AgentWorkspacePaths.AcceptanceCriteriaFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, json);
    }

    #endregion

    #region DetermineFixPromptAction tests

    // TODO: The test below (CriticalFindings) implicitly validates priority ordering by passing both
    // iterationCriticalCount > 0 AND non-empty iterationFindingsText. Consider adding an explicitly
    // named test (e.g., PrioritizesCriticalCountOverFindingsText) to make this coverage more discoverable.

    [Fact]
    public void DetermineFixPromptAction_CriticalFindings_ReturnsSendFixAndContinue()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: false, fixPrompt: "Fix it", iterationCriticalCount: 3, iterationFindingsText: "[CRITICAL] something");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.SendFixAndContinue);
    }

    [Fact]
    public void DetermineFixPromptAction_WarningsOnly_ReturnsSendFixAndBreak()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: false, fixPrompt: "Fix it", iterationCriticalCount: 0, iterationFindingsText: "[WARNING] minor issue");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.SendFixAndBreak);
    }

    [Fact]
    public void DetermineFixPromptAction_NoFindings_ReturnsNoFindingsBreak()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: false, fixPrompt: "Fix it", iterationCriticalCount: 0, iterationFindingsText: "");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.NoFindingsBreak);
    }

    [Fact]
    public void DetermineFixPromptAction_SkipFixPromptTrue_ReturnsSkip()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: true, fixPrompt: "Fix it", iterationCriticalCount: 5, iterationFindingsText: "[CRITICAL] something");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.Skip);
    }

    [Fact]
    public void DetermineFixPromptAction_NullFixPrompt_ReturnsSkip()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: false, fixPrompt: null, iterationCriticalCount: 5, iterationFindingsText: "[CRITICAL] something");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.Skip);
    }

    [Fact]
    public void DetermineFixPromptAction_EmptyFixPrompt_ReturnsSkip()
    {
        var result = AgentPhaseExecutor.DetermineFixPromptAction(
            skipFixPrompt: false, fixPrompt: "", iterationCriticalCount: 5, iterationFindingsText: "[CRITICAL] something");

        result.Should().Be(AgentPhaseExecutor.FixPromptDecision.Skip);
    }

    #endregion

    #region Diff Re-computation and Findings Deletion Tests

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CodeReview_DiffArtifacts_RecomputedPerIteration()
    {
        // Arrange: Create a real git repo so PreComputeDiffArtifactsAsync produces actual artifacts
        var gitWorkspace = Path.Combine(Path.GetTempPath(), $"test-diff-recompute-{Guid.NewGuid():N}");
        Directory.CreateDirectory(gitWorkspace);
        try
        {
            InitGitRepo(gitWorkspace);

            // Create an initial untracked file so first diff has content
            File.WriteAllText(Path.Combine(gitWorkspace, "feature.txt"), "initial implementation\n");

            var run = new PipelineRun
            {
                RunId = "test-run-diff-recompute",
                IssueIdentifier = "99",
                IssueTitle = "Test Issue",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                WorkspacePath = gitWorkspace
            };

            var config = _config with
            {
                CodeReview = new CodeReviewConfiguration
                {
                    MaxIterations = 2,
                    FixPrompt = "Fix the critical issues"
                }
            };

            string? diffStatAfterIteration1 = null;
            var callCount = 0;

            _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
                .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // Iteration 1: review agent — capture current diff stat and write critical findings
                        diffStatAfterIteration1 = File.ReadAllText(
                            Path.Combine(gitWorkspace, AgentWorkspacePaths.DiffStatFilePath));
                        WriteFindingsFileAt(gitWorkspace, "correctness", "[CRITICAL] Bug found");
                    }
                    else if (callCount == 2)
                    {
                        // Fix agent — simulate a code fix by adding + committing a new file
                        File.WriteAllText(Path.Combine(gitWorkspace, "fix.txt"), "bug fix\n");
                        RunGitSync(gitWorkspace, "add .");
                        RunGitSync(gitWorkspace, "commit -m \"fix bug\"");
                    }
                    // callCount == 3: iteration 2 review agent — diff artifacts should be fresh
                })
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

            var context = new AgentPhaseContext
            {
                Run = run,
                Config = config,
                AgentProvider = _mockAgent.Object,
                IssueOps = _mockIssueOps.Object,
                Callbacks = _mockCallbacks.Object,
                OrchestratorCts = null,
                Issue = new IssueDetail { Identifier = "99", Title = "Test Issue", Description = "Test", Labels = new[] { "bug" } },
                ParsedIssue = new ParsedIssue { RequirementsSection = "Requirements", AcceptanceCriteria = new[] { "AC1" } }
            };

            // Act
            await _executor.ExecuteCodeReviewAsync(context, CancellationToken.None, CreateReviewers("Correctness"));

            // Assert: diff stat after iteration 2 should include the fix file (proving re-computation)
            // TODO: Capture diffStatAfterIteration2 inside callCount==3 callback (like diffStatAfterIteration1 at callCount==1)
            // to prove correct temporal ordering — that re-computation happens BEFORE the review agent runs in iteration 2.
            var diffStatAfterIteration2 = File.ReadAllText(
                Path.Combine(gitWorkspace, AgentWorkspacePaths.DiffStatFilePath));

            diffStatAfterIteration1.Should().NotBeNull();
            diffStatAfterIteration1.Should().Contain("feature.txt");
            diffStatAfterIteration1.Should().NotContain("fix.txt");

            diffStatAfterIteration2.Should().Contain("fix.txt", "diff artifacts should be re-computed after fix commit");
            diffStatAfterIteration2.Should().Contain("feature.txt");
        }
        finally
        {
            try { Directory.Delete(gitWorkspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CodeReview_ConsolidatedFindings_DeletedBeforeEachIteration()
    {
        // Arrange: Create a workspace with a .git directory (to pass the guard) and pre-write stale findings
        // TODO: Use InitGitRepo (real git repo) instead of a bare .git directory for robustness.
        // Also consider a multi-iteration variant (maxIterations=2) where iteration 1 writes findings
        // and iteration 2's callback verifies they're gone — directly testing inter-iteration cleanup.
        var gitWorkspace = Path.Combine(Path.GetTempPath(), $"test-findings-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(gitWorkspace);
        Directory.CreateDirectory(Path.Combine(gitWorkspace, ".git")); // minimal — just needs to exist for guard

        try
        {
            var agentDir = Path.Combine(gitWorkspace, AgentWorkspacePaths.MetadataDirectory);
            Directory.CreateDirectory(agentDir);

            // Pre-write a stale consolidated findings file (simulating leftovers from prior iteration)
            var consolidatedPath = Path.Combine(gitWorkspace, AgentWorkspacePaths.ReviewFindingsFilePath);
            File.WriteAllText(consolidatedPath, "[CRITICAL] Stale finding from previous iteration");

            var run = new PipelineRun
            {
                RunId = "test-run-findings-delete",
                IssueIdentifier = "100",
                IssueTitle = "Test Issue",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                WorkspacePath = gitWorkspace
            };

            var config = _config with
            {
                CodeReview = new CodeReviewConfiguration
                {
                    MaxIterations = 1,
                    FixPrompt = null
                }
            };

            var findingsExistedDuringReview = true;

            _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
                .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
                {
                    // At the point the review agent runs, the consolidated findings file should NOT exist
                    findingsExistedDuringReview = File.Exists(consolidatedPath);
                })
                .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

            var context = new AgentPhaseContext
            {
                Run = run,
                Config = config,
                AgentProvider = _mockAgent.Object,
                IssueOps = _mockIssueOps.Object,
                Callbacks = _mockCallbacks.Object,
                OrchestratorCts = null,
                Issue = new IssueDetail { Identifier = "100", Title = "Test Issue", Description = "Test", Labels = new[] { "bug" } },
                ParsedIssue = new ParsedIssue { RequirementsSection = "Requirements", AcceptanceCriteria = new[] { "AC1" } }
            };

            // Act
            await _executor.ExecuteCodeReviewAsync(context, CancellationToken.None, CreateReviewers("Correctness"));

            // Assert: the consolidated findings file was deleted BEFORE the review agent ran
            findingsExistedDuringReview.Should().BeFalse(
                "consolidated findings from prior iteration should be deleted before review agents run");
        }
        finally
        {
            try { Directory.Delete(gitWorkspace, recursive: true); } catch { }
        }
    }

    private static void WriteFindingsFileAt(string workspace, string agentName, string content)
    {
        var relativePath = AgentWorkspacePaths.GetReviewFindingsFilePath(agentName);
        var fullPath = Path.Combine(workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void InitGitRepo(string workspace)
    {
        RunGitSync(workspace, "init");
        RunGitSync(workspace, "config user.email \"test@test.com\"");
        RunGitSync(workspace, "config user.name \"Test\"");
        File.WriteAllText(Path.Combine(workspace, "README.md"), "init\n");
        RunGitSync(workspace, "add .");
        RunGitSync(workspace, "commit -m \"initial\"");
        RunGitSync(workspace, "update-ref refs/remotes/origin/main HEAD");
    }

    private static void RunGitSync(string workspace, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {p.StandardError.ReadToEnd()}");
    }

    #endregion
}
