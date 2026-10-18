using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PipelineCallbacksBase.CreateDraftPrIfNotExists"/>, verifying:
/// <list type="bullet">
/// <item>Successful creation with a PR number emits an output line containing the PR number.</item>
/// <item>Successful creation with no PR number set does not emit an output line.</item>
/// <item>Non-<see cref="OperationCanceledException"/> exceptions are swallowed and routed to <c>LogDraftPrFailure</c>.</item>
/// <item><see cref="OperationCanceledException"/> propagates to the caller.</item>
/// </list>
/// </summary>
public sealed class PipelineCallbacksBaseTests
{
    // ── Test double ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal concrete subclass of <see cref="PipelineCallbacksBase"/> that lets tests configure
    /// the core draft-PR creation behaviour and observe side effects without requiring real
    /// infrastructure.
    /// </summary>
    private sealed class TestCallbacks : PipelineCallbacksBase
    {
        private readonly PipelineRun _run;

        /// <summary>
        /// Delegate called by <see cref="CreateDraftPrCoreAsync"/>. Tests assign this to simulate
        /// success (optionally setting <see cref="PipelineRun.PullRequestNumber"/>), failure, or
        /// cancellation.
        /// </summary>
        public Func<PipelineRun, CancellationToken, Task> CoreAsyncImpl { get; set; }
            = (_, _) => Task.CompletedTask;

        /// <summary>Lines passed to <see cref="EmitOutputLine"/> during the test.</summary>
        public List<string> EmittedLines { get; } = [];

        /// <summary>The exception captured by the overridden <see cref="LogDraftPrFailure"/>, or null.</summary>
        public Exception? CapturedFailureException { get; private set; }

        public TestCallbacks(PipelineRun run) => _run = run;

        // ── Abstract members — no-op stubs ───────────────────────────────

        protected override PipelineRun Run => _run;
        public override void TransitionTo(PipelineStep step) { }
        public override void EmitOutputLine(string line) => EmittedLines.Add(line);
        public override void NotifyChange() { }
        public override Task AddRunToHistoryAsync(PipelineRun run) => Task.CompletedTask;
        public override Task UpdateFileChangeStats(PipelineRun run) => Task.CompletedTask;
        public override Task SwapAgentLabel(IssueIdentifier issueIdentifier, string label, CancellationToken ct) => Task.CompletedTask;
        public override Task RemoveAllAgentLabels(IssueIdentifier issueIdentifier, CancellationToken ct) => Task.CompletedTask;
        public override Task CreatePullRequest(PipelineRun run, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public override Task FinalizePullRequest(PipelineRun run, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public override Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount) => Task.CompletedTask;

        // ── Overridable hooks under test ─────────────────────────────────

        protected override Task CreateDraftPrCoreAsync(PipelineRun run, CancellationToken ct)
            => CoreAsyncImpl(run, ct);

        protected override void LogDraftPrFailure(PipelineRun run, Exception ex)
            => CapturedFailureException = ex;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => PipelineRun.CreateImplementation(new PipelineRunCreationParams
    {
        RunId = "test-run-1",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    });

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion (a): when <c>CreateDraftPrCoreAsync</c> completes successfully and sets
    /// <see cref="PipelineRun.PullRequestNumber"/> to a non-empty string, <see cref="PipelineCallbacksBase.EmitOutputLine"/>
    /// is called with a message that contains the PR number.
    /// </summary>
    [Fact]
    public async Task WhenCoreSucceeds_AndPrNumberIsSet_EmitsOutputLine()
    {
        var run = CreateRun();
        var callbacks = new TestCallbacks(run)
        {
            CoreAsyncImpl = (r, _) =>
            {
                r.PullRequestNumber = "42";
                return Task.CompletedTask;
            }
        };

        await callbacks.CreateDraftPrIfNotExists(run, CancellationToken.None);

        callbacks.EmittedLines.Should().ContainSingle()
            .Which.Should().Contain("42");
    }

    /// <summary>
    /// Acceptance criterion (b): when <c>CreateDraftPrCoreAsync</c> completes with
    /// <see cref="PipelineRun.PullRequestNumber"/> null or empty, <see cref="PipelineCallbacksBase.EmitOutputLine"/>
    /// is not called.
    /// Both values are tested because the production guard uses <see cref="string.IsNullOrEmpty"/>; a
    /// regression that weakens it to a null-only check would silently emit an output line for an empty
    /// PR number — a case the acceptance criteria explicitly calls out.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WhenCoreSucceeds_AndPrNumberIsNullOrEmpty_DoesNotEmitOutputLine(string? prNumber)
    {
        var run = CreateRun();
        var callbacks = new TestCallbacks(run)
        {
            CoreAsyncImpl = (r, _) =>
            {
                r.PullRequestNumber = prNumber;
                return Task.CompletedTask;
            }
        };

        await callbacks.CreateDraftPrIfNotExists(run, CancellationToken.None);

        callbacks.EmittedLines.Should().BeEmpty();
    }

    /// <summary>
    /// Acceptance criterion (c): when <c>CreateDraftPrCoreAsync</c> throws an
    /// <see cref="InvalidOperationException"/>, the exception is swallowed by the exception filter
    /// and <c>LogDraftPrFailure</c> is called with that exception.
    /// </summary>
    [Fact]
    public async Task WhenCoreThrowsNonOce_ExceptionIsSwallowed_AndLogDraftPrFailureIsCalled()
    {
        var run = CreateRun();
        var thrown = new InvalidOperationException("draft PR creation failed");
        var callbacks = new TestCallbacks(run)
        {
            CoreAsyncImpl = (_, _) => throw thrown
        };

        // Must not throw.
        await callbacks.CreateDraftPrIfNotExists(run, CancellationToken.None);

        callbacks.CapturedFailureException.Should().BeSameAs(thrown);
        callbacks.EmittedLines.Should().BeEmpty();
    }

    /// <summary>
    /// Acceptance criterion (d): when <c>CreateDraftPrCoreAsync</c> throws an
    /// <see cref="OperationCanceledException"/>, the exception filter rejects it and it propagates
    /// to the caller.
    /// </summary>
    [Fact]
    public async Task WhenCoreThrowsOperationCanceledException_ExceptionPropagates()
    {
        var run = CreateRun();
        var callbacks = new TestCallbacks(run)
        {
            CoreAsyncImpl = (_, _) => throw new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => callbacks.CreateDraftPrIfNotExists(run, CancellationToken.None));

        // The exception filter skipped LogDraftPrFailure.
        callbacks.CapturedFailureException.Should().BeNull();
        callbacks.EmittedLines.Should().BeEmpty();
    }
}
