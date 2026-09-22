using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelinePollingHelper"/>.
/// Delegates are plain lambdas — no mock framework required.
/// </summary>
public class PipelinePollingHelperTests
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    // --- PollUntilCompleteAsync ---

    [Fact]
    public async Task PollUntilCompleteAsync_TerminalSuccess_ReturnsOnFirstTerminalPoll()
    {
        var enrichCalled = false;
        var passedStatus = MakeStatus(PipelineRunState.Passed, "sha1");

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ => Task.FromResult(passedStatus),
            enrichFailedJobsAsync: (s, _) => { enrichCalled = true; return Task.FromResult(s); },
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromSeconds(5),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        result.State.Should().Be(PipelineRunState.Passed);
        enrichCalled.Should().BeFalse("enrichment is only called for Failed runs");
    }

    [Fact]
    public async Task PollUntilCompleteAsync_TerminalFailure_CallsEnrichment()
    {
        var enrichCallCount = 0;
        var failedStatus = MakeStatus(PipelineRunState.Failed, "sha2");
        var enrichedStatus = MakeStatus(PipelineRunState.Failed, "sha2", logContent: "error logs");

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ => Task.FromResult(failedStatus),
            enrichFailedJobsAsync: (s, _) =>
            {
                enrichCallCount++;
                return Task.FromResult(enrichedStatus);
            },
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromSeconds(5),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        result.State.Should().Be(PipelineRunState.Failed);
        enrichCallCount.Should().Be(1, "enrichment must be called exactly once for a failed run");
        result.Jobs.Should().HaveCount(1);
        result.Jobs[0].LogContent.Should().Be("error logs");
        // TODO: The assertion above is mildly tautological — it confirms the helper returns
        // whatever the enrichment delegate returns, but cannot detect a bug where enrichment is
        // called yet its return value is discarded in favour of the original status. Add
        // result.Should().BeSameAs(enrichedStatus) to close this gap, ensuring the enriched
        // instance (not the original failedStatus) is the one returned.
    }

    [Fact]
    public async Task PollUntilCompleteAsync_TerminalCancelled_ReturnsWithoutEnrichment()
    {
        var enrichCalled = false;
        var cancelledStatus = MakeStatus(PipelineRunState.Cancelled, "sha3");

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ => Task.FromResult(cancelledStatus),
            enrichFailedJobsAsync: (s, _) => { enrichCalled = true; return Task.FromResult(s); },
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromSeconds(5),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        result.State.Should().Be(PipelineRunState.Cancelled);
        enrichCalled.Should().BeFalse("enrichment is only called for Failed, not Cancelled");
    }

    [Fact]
    public async Task PollUntilCompleteAsync_NonTerminalThenTerminal_PollsMultipleTimes()
    {
        var pollCount = 0;

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ =>
            {
                pollCount++;
                var state = pollCount < 3 ? PipelineRunState.Running : PipelineRunState.Passed;
                return Task.FromResult(MakeStatus(state, "sha4"));
            },
            enrichFailedJobsAsync: (s, _) => Task.FromResult(s),
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromSeconds(10),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        result.State.Should().Be(PipelineRunState.Passed);
        pollCount.Should().BeGreaterThanOrEqualTo(3, "must have polled through non-terminal states before reaching Passed");
    }

    [Fact]
    public async Task PollUntilCompleteAsync_Timeout_ReturnsLastStatus()
    {
        // TODO: This test uses a single unchanging status object for all polls, so it cannot
        // distinguish "returns the LAST status" from "returns any status ever seen". To properly
        // verify the "last" semantics, use a multi-step scenario where the status changes between
        // polls (e.g. sha-first then sha-last) and assert the final returned CommitSha equals
        // the value from the most-recent poll before the timeout.
        var runningStatus = MakeStatus(PipelineRunState.Running, "sha5");

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ => Task.FromResult(runningStatus),
            enrichFailedJobsAsync: (s, _) => Task.FromResult(s),
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromMilliseconds(80),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        result.State.Should().Be(PipelineRunState.Running, "timeout should return the last observed status");
        result.CommitSha.Should().Be("sha5");
    }

    [Fact]
    public async Task PollUntilCompleteAsync_Timeout_NullLastStatus_ReturnsPendingWithNullCommitSha()
    {
        // TODO: This test is environment-sensitive. It simulates a null lastStatus by blocking the
        // first poll for 10 seconds with a 100ms timeout, relying on wall-clock timing to ensure
        // the timeout fires before the poll returns. On slow CI machines the timing may not hold.
        // A more deterministic approach: provide a getRunStatusAsync that immediately throws
        // OperationCanceledException when its linkedCt is cancelled, then assert the fallback
        // Pending result. This avoids relying on a 100ms wall-clock race.
        // Simulate timeout firing before the first poll returns by using a very long delay
        using var cts = new CancellationTokenSource();
        var pollStarted = false;

        var result = await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: async linkedCt =>
            {
                pollStarted = true;
                // Delay long enough that the timeout fires — the test timeout is 100ms
                await Task.Delay(TimeSpan.FromSeconds(10), linkedCt);
                return MakeStatus(PipelineRunState.Running, "sha6");
            },
            enrichFailedJobsAsync: (s, _) => Task.FromResult(s),
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromMilliseconds(100),
            logPrefix: "CI",
            ct: CancellationToken.None,
            logger: SilentLogger);

        pollStarted.Should().BeTrue();
        result.State.Should().Be(PipelineRunState.Pending,
            "when no poll completes before timeout, lastStatus is null so Pending is returned");
    }

    [Fact]
    public async Task PollUntilCompleteAsync_CallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: _ => Task.FromResult(MakeStatus(PipelineRunState.Running, "sha7")),
            enrichFailedJobsAsync: (s, _) => Task.FromResult(s),
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: TimeSpan.FromMilliseconds(10),
            timeout: TimeSpan.FromSeconds(5),
            logPrefix: "CI",
            ct: cts.Token,
            logger: SilentLogger);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must not be swallowed by the timeout handler");
    }

    // --- EnrichFailedJobsWithLogsAsync ---

    [Fact]
    public async Task EnrichFailedJobsWithLogsAsync_NoFailedJobs_ReturnsSameStatus()
    {
        var fetchCalled = false;
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Passed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Passed, JobId = 1 }
            },
            CommitSha = "sha"
        };

        var result = await PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
            status,
            (_, _) => { fetchCalled = true; return Task.FromResult<string?>(null); },
            "job",
            CancellationToken.None,
            SilentLogger);

        result.Should().BeSameAs(status, "no failed jobs means no enrichment and original object returned");
        fetchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task EnrichFailedJobsWithLogsAsync_JobIdZero_Skipped()
    {
        var fetchCalled = false;
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, JobId = 0 }
            },
            CommitSha = "sha"
        };

        var result = await PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
            status,
            (_, _) => { fetchCalled = true; return Task.FromResult<string?>("logs"); },
            "job",
            CancellationToken.None,
            SilentLogger);

        result.Should().BeSameAs(status, "jobs with JobId=0 have no valid API identifier and must be skipped");
        fetchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task EnrichFailedJobsWithLogsAsync_LogFetchFails_JobHasNullLogContent()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, JobId = 42 }
            },
            CommitSha = "sha"
        };

        var result = await PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
            status,
            (_, _) => Task.FromResult<string?>(null),
            "job",
            CancellationToken.None,
            SilentLogger);

        // When all fetches return null, logsByJobId is empty → original status returned
        result.Should().BeSameAs(status);
        result.Jobs[0].LogContent.Should().BeNull();
    }

    [Fact]
    public async Task EnrichFailedJobsWithLogsAsync_LogFetchSucceeds_InjectsLogContent()
    {
        const long jobId = 99;
        const string expectedLog = "compilation error on line 42";

        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "test", State = PipelineRunState.Failed, JobId = jobId }
            },
            CommitSha = "sha"
        };

        var result = await PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
            status,
            (id, _) => Task.FromResult<string?>(id == jobId ? expectedLog : null),
            "job",
            CancellationToken.None,
            SilentLogger);

        result.Should().NotBeSameAs(status, "a new status instance with enriched jobs must be returned");
        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs.Should().HaveCount(1);
        result.Jobs[0].LogContent.Should().Be(expectedLog);
        result.Jobs[0].Name.Should().Be("test");
        result.Jobs[0].JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task EnrichFailedJobsWithLogsAsync_AllLogsEmpty_ReturnsSameStatus()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "a", State = PipelineRunState.Failed, JobId = 1 },
                new PipelineJobResult { Name = "b", State = PipelineRunState.Failed, JobId = 2 }
            },
            CommitSha = "sha"
        };

        var result = await PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
            status,
            (_, _) => Task.FromResult<string?>(null),
            "job",
            CancellationToken.None,
            SilentLogger);

        result.Should().BeSameAs(status,
            "when all fetches return null the original status is returned unchanged");
    }

    // --- helpers ---

    private static PipelineRunStatus MakeStatus(
        PipelineRunState state,
        string commitSha,
        string? logContent = null)
    {
        return new PipelineRunStatus
        {
            State = state,
            CommitSha = commitSha,
            Jobs = logContent is null
                ? Array.Empty<PipelineJobResult>()
                : new[]
                {
                    new PipelineJobResult
                    {
                        Name = "job",
                        State = state,
                        JobId = 1,
                        LogContent = logContent
                    }
                }
        };
    }
}
