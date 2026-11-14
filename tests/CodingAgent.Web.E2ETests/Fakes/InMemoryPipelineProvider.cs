using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// One CI run of one commit, as <see cref="InMemoryPipelineProvider"/> serves it. Set one of the four
/// members; when several are set, <see cref="NotStarted"/> wins over <see cref="Error"/>, which wins
/// over <see cref="Hangs"/>, which wins over <see cref="Status"/>.
/// </summary>
public sealed record ScriptedCiRound
{
    /// <summary>What <see cref="IPipelineProvider.WaitForCompletionAsync"/> returns.</summary>
    public PipelineRunStatus? Status { get; init; }

    /// <summary>What <see cref="IPipelineProvider.WaitForCompletionAsync"/> throws.</summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// CI never appears: the status stays <see cref="PipelineRunState.Pending"/> with no jobs. The round
    /// is never consumed, so it stays at the head of the queue until <see cref="InMemoryPipelineProvider.Reset"/>
    /// and the rounds behind it are unreachable.
    /// </summary>
    public bool NotStarted { get; init; }

    /// <summary><see cref="IPipelineProvider.WaitForCompletionAsync"/> waits until its token is cancelled.</summary>
    public bool Hangs { get; init; }
}

/// <summary>
/// In-memory pipeline provider for E2E tests. Simulates external CI pass/fail.
///
/// <para>
/// With an empty queue every member answers from <see cref="ShouldPass"/>, as it always did. Enqueue
/// <see cref="ScriptedCiRound"/>s to script a run: one round is one CI run of one commit, and it is
/// consumed by one <see cref="WaitForCompletionAsync"/>. <see cref="GetRunStatusAsync"/> only looks at the
/// head round, so the pipeline's "has CI appeared" poll does not use one up. When the queue drains, the
/// default pass or fail answers again.
/// </para>
///
/// <para>
/// A worker run that waits for CI three times (before the PR is finalized, for the final quality gates
/// after the cleanup, and after the PR) consumes three rounds.
/// </para>
///
/// <para>
/// The worker only gets this provider when its assignment names a pipeline provider and the config store
/// holds a <see cref="ProviderKind.Pipeline"/> config with that id; see <c>ScriptedCiSmokeTests</c>.
/// <see cref="StatusCalls"/>, <see cref="WaitCalls"/> and <see cref="ValidateCalls"/> show whether it did.
/// Read them once the worker has finished: the worker writes them under a lock the test does not take.
/// </para>
/// </summary>
public sealed class InMemoryPipelineProvider : IPipelineProvider
{
    private const string DefaultJobName = "build-and-test";
    private const string DefaultJobLog = "Fake CI log output";

    private readonly object _gate = new();
    private readonly Queue<ScriptedCiRound> _rounds = new();

    public PipelineProviderType ProviderType => PipelineProviderType.GitHubActions;
    public bool ShouldPass { get; set; } = true;
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Log text by job id, for <see cref="GetJobLogsAsync"/>. The product reads
    /// <see cref="PipelineJobResult.LogContent"/>, not this; <see cref="EnqueueFailure"/> fills both.
    /// </summary>
    public Dictionary<long, string?> JobLogs { get; } = new();

    /// <summary>Every <see cref="GetRunStatusAsync"/> call, in order. A wait is not a status call.</summary>
    public List<(string Branch, string? CommitSha)> StatusCalls { get; } = new();

    /// <summary>Every <see cref="WaitForCompletionAsync"/> call, in order.</summary>
    public List<(string Branch, string? CommitSha)> WaitCalls { get; } = new();

    /// <summary>How often <see cref="ValidateAsync"/> was called. The worker calls it when it resolves the provider.</summary>
    public int ValidateCalls { get; private set; }

    public void Reset()
    {
        ShouldPass = true;
        SimulatedDelay = TimeSpan.Zero;

        lock (_gate)
        {
            _rounds.Clear();
            JobLogs.Clear();
            StatusCalls.Clear();
            WaitCalls.Clear();
            ValidateCalls = 0;
        }
    }

    // TODO [WARNING] (TestQualityReviewer): The Enqueue argument guard (an empty round throws ArgumentException, null
    // throws ArgumentNullException) and the documented precedence (NotStarted over Error over Hangs over Status) are
    // behavior the issue does not specify, and no test covers either. Dropping the guard or reordering the
    // precedence would leave every test green. Not added here: a sixth test conflicts with the issue's acceptance
    // criterion of exactly five tests in ScriptedCiSmokeTests.
    /// <summary>Appends a round to the queue.</summary>
    public InMemoryPipelineProvider Enqueue(ScriptedCiRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Status is null && round.Error is null && !round.NotStarted && !round.Hangs)
            throw new ArgumentException("A scripted CI round needs a Status, an Error, NotStarted or Hangs.", nameof(round));

        lock (_gate)
        {
            _rounds.Enqueue(round);
        }

        return this;
    }

    /// <summary>A round that passes: one job <c>build-and-test</c> with id 1.</summary>
    public InMemoryPipelineProvider EnqueuePass() => Enqueue(new ScriptedCiRound
    {
        Status = new PipelineRunStatus
        {
            State = PipelineRunState.Passed,
            Jobs = new[] { BuildJob(DefaultJobName, 1, PipelineRunState.Passed) }
        }
    });

    /// <summary>
    /// A round that fails: one failed job whose <see cref="PipelineJobResult.LogContent"/> is
    /// <paramref name="log"/>, which is what the pipeline reads. The same log is also served by
    /// <see cref="GetJobLogsAsync"/>. A failed job without a log counts as an infrastructure failure
    /// and is retried without the fix agent.
    /// </summary>
    public InMemoryPipelineProvider EnqueueFailure(string jobName, long jobId, string log, params string[] failedSteps)
    {
        lock (_gate)
        {
            JobLogs[jobId] = log;
        }

        return Enqueue(new ScriptedCiRound
        {
            Status = new PipelineRunStatus
            {
                State = PipelineRunState.Failed,
                Jobs = new[]
                {
                    new PipelineJobResult
                    {
                        Name = jobName,
                        State = PipelineRunState.Failed,
                        FailureReason = "Tests failed",
                        JobId = jobId,
                        LogContent = log,
                        FailedSteps = failedSteps
                    }
                }
            }
        });
    }

    /// <summary>
    /// A round whose wait throws <paramref name="error"/>. The status read stays <see cref="PipelineRunState.Running"/>:
    /// the pipeline swallows an error from the status read that decides whether CI has appeared.
    /// </summary>
    public InMemoryPipelineProvider EnqueueError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Enqueue(new ScriptedCiRound { Error = error });
    }

    /// <summary>A round where CI never appears. It is never consumed; see <see cref="ScriptedCiRound.NotStarted"/>.</summary>
    public InMemoryPipelineProvider EnqueueNotStarted() => Enqueue(new ScriptedCiRound { NotStarted = true });

    /// <summary>A round whose wait never returns, until its token is cancelled.</summary>
    public InMemoryPipelineProvider EnqueueHang() => Enqueue(new ScriptedCiRound { Hangs = true });

    public async Task<PipelineRunStatus> GetRunStatusAsync(BranchName branchName, string? commitSha, CancellationToken ct)
    {
        ScriptedCiRound? head;
        lock (_gate)
        {
            StatusCalls.Add((branchName.Value, commitSha));
            head = _rounds.TryPeek(out var round) ? round : null;
        }

        if (SimulatedDelay > TimeSpan.Zero)
            await Task.Delay(SimulatedDelay, ct);

        if (head is null)
            return BuildDefaultStatus();
        if (head.NotStarted)
            return BuildPendingStatus();
        if (head.Error is not null || head.Hangs)
            return BuildRunningStatus();

        return head.Status!;
    }

    public async Task<PipelineRunStatus> WaitForCompletionAsync(BranchName branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
    {
        ScriptedCiRound? round = null;
        var notStarted = false;
        lock (_gate)
        {
            WaitCalls.Add((branchName.Value, commitSha));
            if (_rounds.TryPeek(out var head))
            {
                if (head.NotStarted)
                    notStarted = true;
                else
                    round = _rounds.Dequeue();
            }
        }

        if (SimulatedDelay > TimeSpan.Zero)
            await Task.Delay(SimulatedDelay, ct);

        if (notStarted)
            return BuildPendingStatus();
        if (round is null)
            return BuildDefaultStatus();
        if (round.Error is not null)
            throw round.Error;

        if (round.Hangs)
        {
            // Ends only by cancelling ct, which throws; nothing after it runs for a hanging round.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return BuildRunningStatus();
        }

        return round.Status!;
    }

    public Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(JobLogs.TryGetValue(jobId, out var log) ? log : DefaultJobLog);
        }
    }

    public Task ValidateAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ValidateCalls++;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private PipelineRunStatus BuildDefaultStatus()
    {
        var state = ShouldPass ? PipelineRunState.Passed : PipelineRunState.Failed;
        return new PipelineRunStatus
        {
            State = state,
            Jobs = new[]
            {
                new PipelineJobResult
                {
                    Name = DefaultJobName,
                    State = state,
                    FailureReason = ShouldPass ? null : "Tests failed"
                }
            }
        };
    }

    private static PipelineRunStatus BuildPendingStatus() =>
        new() { State = PipelineRunState.Pending, Jobs = [] };

    private static PipelineRunStatus BuildRunningStatus() =>
        new() { State = PipelineRunState.Running, Jobs = new[] { BuildJob(DefaultJobName, 1, PipelineRunState.Running) } };

    private static PipelineJobResult BuildJob(string name, long jobId, PipelineRunState state) =>
        new() { Name = name, State = state, JobId = jobId };
}
