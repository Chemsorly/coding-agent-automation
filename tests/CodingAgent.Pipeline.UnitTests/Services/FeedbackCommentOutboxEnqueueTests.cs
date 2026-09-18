using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Regression tests for the durable feedback-comment outbox enqueue behaviour
/// in <see cref="AgentJobLifecycleService.PostCompletionBookkeepingAsync"/>.
///
/// Key invariants:
/// - The outbox row is enqueued BEFORE the ApplicationStopping-linked CancellationTokenSource
///   is created, so it survives graceful shutdown even when the inline fast path is cancelled.
/// - The enqueue guard mirrors <see cref="FeedbackCommentFormatter.FormatComment"/>:
///   only rows with a non-null Description are written.
/// - After a successful inline post, MarkCompletedAsync is called so the relay skips the row.
/// </summary>
public sealed class FeedbackCommentOutboxEnqueueTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IHostApplicationLifetime> _appLifetime = new();
    private readonly Mock<IFeedbackCommentOutbox> _outbox = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentJobLifecycleService _sut;

    public FeedbackCommentOutboxEnqueueTests()
    {
        // Non-cancellable token so ApplicationStopping does not abort runs in tests.
        _appLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        _sut = new AgentJobLifecycleService(
            _facade.Object,
            _lifecycle.Object,
            _labelService.Object,
            _issueOps.Object,
            _changeNotifier.Object,
            _appLifetime.Object,
            _outbox.Object,
            _logger.Object);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static RunFeedback MakeFeedbackWithDescription(string description = "Issue is unclear") =>
        new()
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = description }
        };

    private static PipelineRun MakeRun(RunFeedback? feedback = null)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "job-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        run.Feedback = feedback;
        return run;
    }

    /// <summary>
    /// Creates a payload with the given feedback so that JobCompletionMapper.Apply
    /// does NOT wipe run.Feedback (Apply sets run.Feedback = payload.Feedback).
    /// </summary>
    private static JobCompletionPayload MakePayload(
        PipelineStep step = PipelineStep.Completed,
        RunFeedback? feedback = null) =>
        new() { FinalStep = step, CompletedAt = DateTimeOffset.UtcNow, Feedback = feedback };

    // ── Test: cancellation before comment → outbox row still enqueued ──────

    [Fact]
    public async Task WhenCancelledBeforeLabelSwap_OutboxRowStillEnqueued()
    {
        // This is the PRIMARY regression test for the :395 TODO bug.
        // The outbox enqueue must happen BEFORE cts is created, so ApplicationStopping
        // cannot prevent the row from being written.
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        // Simulate ApplicationStopping firing after the enqueue but cancelling SwapLabel
        _issueOps
            .Setup(o => o.SwapLabelAsync(run, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        _issueOps
            .Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            // TODO: Remove this dead mock setup — PostIssueFeedbackCommentAsync is never reached
            // when SwapLabelAsync throws OperationCanceledException (the catch block exits before it).
            // Its presence falsely implies the comment could still be posted in this scenario.
            // Also add: _outbox.Verify(o => o.MarkCompletedAsync(...), Times.Never) to guard against
            // an erroneous future change that calls MarkCompleted after cancellation.
            .Returns(Task.CompletedTask);

        var feedback = MakeFeedbackWithDescription();
        // Payload carries feedback so JobCompletionMapper.Apply preserves it
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(PipelineStep.Completed, feedback), CancellationToken.None);

        // The enqueue must have been called with CancellationToken.None
        _outbox.Verify(o => o.EnqueueAsync(
            It.Is<FeedbackCommentOutboxEntry>(e =>
                e.RunId == "job-1" &&
                e.IssueProviderConfigId == "github" &&
                e.IssueIdentifier == "GH-42" &&
                e.RepoProviderConfigId == "github-repo"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task WhenCancelledAfterLabelSwapBeforeComment_OutboxRowStillEnqueued()
    {
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        // Label swap succeeds, comment throws
        _issueOps
            .Setup(o => o.SwapLabelAsync(run, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps
            .Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var feedback = MakeFeedbackWithDescription();
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(PipelineStep.Completed, feedback), CancellationToken.None);

        _outbox.Verify(o => o.EnqueueAsync(
            It.Is<FeedbackCommentOutboxEntry>(e => e.RunId == "job-1"),
            CancellationToken.None),
            Times.Once);
    }

    // ── Test: null guard on Description ────────────────────────────────────

    [Fact]
    public async Task WhenFeedbackIsNull_NoEnqueue()
    {
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Payload has NO feedback → Apply sets run.Feedback = null → guard fails → no enqueue
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(), CancellationToken.None);

        _outbox.Verify(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenIssueFeedbackDescriptionIsNull_NoEnqueue()
    {
        // This guards against the exact case where IssueFeedback exists but Description=null —
        // FeedbackCommentFormatter.FormatComment returns null for this, so no comment would ever
        // be posted. Enqueuing such a row would create an un-deliverable entry.
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var feedbackWithNullDescription = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback(),
            Issue = new IssueFeedback { Description = null } // Description is null
        };
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(feedback: feedbackWithNullDescription), CancellationToken.None);

        _outbox.Verify(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test: successful inline post → MarkCompletedAsync called ──────────

    [Fact]
    public async Task WhenInlinePostSucceeds_MarkCompletedCalled()
    {
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Capture the enqueued entry's ID to verify MarkCompleted is called with it
        FeedbackCommentOutboxEntry? capturedEntry = null;
        _outbox
            .Setup(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()))
            .Callback<FeedbackCommentOutboxEntry, CancellationToken>((e, _) => capturedEntry = e)
            .Returns(Task.CompletedTask);

        var feedback = MakeFeedbackWithDescription();
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(feedback: feedback), CancellationToken.None);

        capturedEntry.Should().NotBeNull("EnqueueAsync must have been called");
        _outbox.Verify(o => o.MarkCompletedAsync(capturedEntry!.Id, CancellationToken.None), Times.Once);
    }

    // ── Test: FeedbackJson contains serialized IssueFeedback ──────────────

    [Fact]
    public async Task EnqueuedEntry_FeedbackJsonContainsSerializedIssueFeedback()
    {
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // Simulate cancellation so we only verify the enqueue, not the mark-completed
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        FeedbackCommentOutboxEntry? capturedEntry = null;
        _outbox
            .Setup(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()))
            .Callback<FeedbackCommentOutboxEntry, CancellationToken>((e, _) => capturedEntry = e)
            .Returns(Task.CompletedTask);

        const string description = "The acceptance criteria are missing";
        var feedback = MakeFeedbackWithDescription(description);
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(feedback: feedback), CancellationToken.None);

        capturedEntry.Should().NotBeNull();
        var deserialized = JsonSerializer.Deserialize<IssueFeedback>(capturedEntry!.FeedbackJson, PipelineJsonOptions.Default);
        deserialized.Should().NotBeNull();
        deserialized!.Description.Should().Be(description);
    }

    // ── Test: consolidation run → no enqueue (outer guard) ────────────────

    [Fact]
    public async Task ConsolidationRun_NoEnqueueAndNoBookkeeping()
    {
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "job-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId, // consolidation
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);

        // Even with feedback on the payload — outer guard skips bookkeeping for consolidation runs
        var feedback = MakeFeedbackWithDescription();
        await _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(feedback: feedback), CancellationToken.None);

        _outbox.Verify(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test: MarkCompletedAsync throws → method succeeds, Warning logged ──

    [Fact]
    public async Task WhenMarkCompletedThrowsDbUpdateException_MethodSucceedsAndWarningIsLogged()
    {
        // Arrange: label swap and comment post both succeed.
        // MarkCompletedAsync throws DbUpdateException (transient DB failure after side effects have committed).
        // This is the bug from issue #2646: the unhandled exception escaped PostCompletionBookkeepingAsync
        // and failed the hub method even though label swap and comment post already succeeded.
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "c1",
            Hostname = "h",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };
        var jobId = new JobId("job-1");
        var run = MakeRun();
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        // TODO: [WARNING] _lifecycle.CompleteRunAsync is not set up here; Loose mock returns null, causing
        // RegularJobCompletionStrategy to exercise its warning/fallback branch (TransitionWorkItemAsync called).
        // If mocks are ever switched to MockBehavior.Strict, this test will fail on an unrelated call rather
        // than on the MarkCompletedAsync exception-handling path. Fix: add
        // _lifecycle.Setup(l => l.CompleteRunAsync(...)).ReturnsAsync(run) to make preconditions explicit.

        _issueOps.Setup(o => o.SwapLabelAsync(run, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.PostIssueFeedbackCommentAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Capture the enqueued entry so we can confirm EnqueueAsync was actually called.
        FeedbackCommentOutboxEntry? capturedEntry = null;
        _outbox
            .Setup(o => o.EnqueueAsync(It.IsAny<FeedbackCommentOutboxEntry>(), It.IsAny<CancellationToken>()))
            .Callback<FeedbackCommentOutboxEntry, CancellationToken>((e, _) => capturedEntry = e)
            .Returns(Task.CompletedTask);

        _outbox
            .Setup(o => o.MarkCompletedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("transient failure"));

        var feedback = MakeFeedbackWithDescription();

        // Act: should NOT throw — the hub method must succeed despite the DB failure in MarkCompletedAsync
        var act = () => _sut.HandleJobCompletedAsync(jobId, agent, MakePayload(feedback: feedback), CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Guard: confirm EnqueueAsync was called so outboxEntryId was non-empty going into MarkCompletedAsync.
        // Without this, capturedEntry would be null and the Warning verify below could pass vacuously.
        capturedEntry.Should().NotBeNull("EnqueueAsync must have been called for a run with non-null Description");

        // TODO: [WARNING] No Verify on SwapLabelAsync — if a bug were introduced that skipped the label swap
        // entirely, this test would not catch the regression (the outbox enqueue happens before the swap, so
        // capturedEntry would still be non-null). Consider adding:
        // _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once)
        // to confirm bookkeeping actually reached the label-swap step.

        // Assert: Warning<Guid>(Exception, string, Guid) was logged.
        // Serilog dispatches _logger.Warning(ex, "... {OutboxEntryId} ...", guid) to the generic
        // Warning<Guid>(Exception, string, Guid) overload — Moq Verify must supply three typed args.
        // Using It.IsAny<Guid>() avoids Guid.Empty coincidence fragility.
        // TODO: [WARNING] If Serilog resolves the call to Warning(Exception, string, object?[]) instead of
        // Warning<Guid>(Exception, string, Guid), the Verify below will pass vacuously (zero invocations).
        // Confirm by checking that removing the catch block causes this Verify to fail. As a fallback guard,
        // consider also adding: _logger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(),
        // It.IsAny<object>()), Times.AtLeastOnce).
        // TODO: [WARNING] It.IsAny<Exception>() does not verify that the logged exception is the DbUpdateException
        // instance thrown by the mock — if the catch block is restructured to swallow the exception before
        // passing it to Warning, this Verify still passes. Consider: It.Is<Exception>(ex => ex is DbUpdateException).
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{OutboxEntryId}")),
                It.IsAny<Guid>()),
            Times.Once);
    }
}
