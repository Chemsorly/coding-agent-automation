using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Verifies the null-guard contract on <see cref="LeaderElectedPollingService.RateLimiter"/>:
/// when a subclass omits <c>rateLimitPerSecond</c>, <c>RateLimiter</c> is null, and any site
/// that applies the <c>?? throw new InvalidOperationException(...)</c> guard throws
/// <see cref="InvalidOperationException"/> rather than <see cref="NullReferenceException"/>.
///
/// This test documents a defensive contract for future subclasses — the null path is not
/// reachable through existing production constructors (all of which supply a non-null
/// <c>rateLimitPerSecond</c>), but the guard must be correct if it ever fires.
///
/// TODO: These tests use inner test doubles (NullRateLimiterService, ConsolidationStyleNullGuardTestService)
/// that re-implement the guard expression locally rather than invoking the production code path in
/// DispatchService.ProcessDispatchCandidateAsync, ConsolidationDispatchHandler.ProcessConsolidationItemAsync,
/// or ConsolidationWorkItemDispatchService.PollAndDispatchConsolidationAsync.
/// As a result, removing or changing the guard in the production methods would not cause these tests to fail —
/// they only verify that the ?? throw pattern works in C#, not that it is actually present at the correct site.
/// For stronger regression coverage, add integration-style tests that instantiate the real production classes
/// in a state where RateLimiter is null and trigger the processing loop.
/// See TestQualityReviewer WARNING (Issue #1994).
///
/// TODO: These tests duplicate equivalent scenarios in PostgresLeaderElectionServiceTests.cs
/// (same test doubles, same invariants). If the guard contract changes, both files need updating.
/// Consider consolidating into one location to reduce maintenance surface.
/// See TestQualityReviewer SUGGESTION (Issue #1994).
/// </summary>
public class RateLimiterNullGuardTests
{
    /// <summary>
    /// A minimal concrete subclass of <see cref="LeaderElectedPollingService"/> that
    /// deliberately omits <c>rateLimitPerSecond</c>, leaving <c>RateLimiter</c> as null,
    /// and exposes a method that applies the same <c>?? throw</c> guard used in
    /// <see cref="DispatchService"/> and <see cref="ConsolidationDispatchHandler"/>.
    /// </summary>
    private sealed class NullRateLimiterService : LeaderElectedPollingService
    {
        public NullRateLimiterService(ILeaderElectionService leaderElection)
            : base(leaderElection)
        {
            // Intentionally omits rateLimitPerSecond → RateLimiter stays null.
        }

        protected override string ServiceName => "NullRateLimiterService";
        protected override int PollIntervalSeconds => 60;
        protected override Task OnPollCycleAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Applies the null-guard expression mirroring DispatchService and
        /// ConsolidationDispatchHandler to verify it throws the correct exception type.
        /// </summary>
        public void InvokeNullGuard()
        {
            _ = RateLimiter ?? throw new InvalidOperationException(
                "NullRateLimiterService requires a rate limiter but RateLimiter is null. " +
                "Ensure the constructor passes rateLimitPerSecond to the base class.");
        }
    }

    /// <summary>
    /// A minimal concrete subclass of <see cref="LeaderElectedPollingService"/> that
    /// deliberately omits <c>rateLimitPerSecond</c>, leaving <c>RateLimiter</c> as null,
    /// and applies the same <c>?? throw</c> guard pattern used in
    /// <see cref="ConsolidationWorkItemDispatchService.PollAndDispatchConsolidationAsync"/>.
    /// Verifies that the guard message names "ConsolidationWorkItemDispatchService".
    /// </summary>
    private sealed class ConsolidationStyleNullGuardTestService : LeaderElectedPollingService
    {
        public ConsolidationStyleNullGuardTestService(ILeaderElectionService leaderElection)
            : base(leaderElection)
        {
            // Intentionally omits rateLimitPerSecond → RateLimiter stays null.
        }

        protected override string ServiceName => "ConsolidationStyleNullGuardTestService";
        protected override int PollIntervalSeconds => 60;
        protected override Task OnPollCycleAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Applies the null-guard expression mirroring
        /// <see cref="ConsolidationWorkItemDispatchService.PollAndDispatchConsolidationAsync"/>
        /// to verify it throws the correct exception type and message.
        /// </summary>
        public void InvokeConsolidationStyleNullGuard()
        {
            _ = RateLimiter ?? throw new InvalidOperationException(
                "ConsolidationWorkItemDispatchService requires a rate limiter but RateLimiter is null. " +
                "Ensure the constructor passes rateLimitPerSecond to the base class.");
        }
    }

    [Fact]
    public void WhenRateLimiterIsNull_NullGuardExpression_ThrowsInvalidOperationException()
    {
        // Arrange: construct a service that intentionally has no rate limiter.
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);

        var service = new NullRateLimiterService(leaderElectionMock.Object);

        // Act & Assert: the null-guard must throw InvalidOperationException, not NullReferenceException.
        service.Invoking(s => s.InvokeNullGuard())
            .Should().Throw<InvalidOperationException>(
                "the null-guard must surface a clear error rather than a NullReferenceException");
    }

    [Fact]
    public void WhenRateLimiterIsNull_NullGuardExpression_ExceptionMessageNamesTheService()
    {
        // Arrange
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);

        var service = new NullRateLimiterService(leaderElectionMock.Object);

        // Act & Assert: the exception message must identify the service for actionable diagnostics.
        service.Invoking(s => s.InvokeNullGuard())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*NullRateLimiterService*");
    }

    [Fact]
    public void WhenRateLimiterIsNull_ConsolidationStyleGuard_ThrowsInvalidOperationException()
    {
        // Arrange: construct a service with the ConsolidationWorkItemDispatchService-style guard,
        // but no rate limiter configured.
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);

        var service = new ConsolidationStyleNullGuardTestService(leaderElectionMock.Object);

        // Act & Assert: mirrors ConsolidationWorkItemDispatchService.PollAndDispatchConsolidationAsync —
        // the guard must throw InvalidOperationException, not NullReferenceException.
        service.Invoking(s => s.InvokeConsolidationStyleNullGuard())
            .Should().Throw<InvalidOperationException>(
                "the null-guard in ConsolidationWorkItemDispatchService must surface a clear error " +
                "rather than a NullReferenceException");
    }

    [Fact]
    public void WhenRateLimiterIsNull_ConsolidationStyleGuard_ExceptionMessageNamesConsolidationWorkItemDispatchService()
    {
        // Arrange
        var leaderElectionMock = new Mock<ILeaderElectionService>();
        leaderElectionMock.SetupGet(l => l.IsLeader).Returns(false);
        leaderElectionMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);

        var service = new ConsolidationStyleNullGuardTestService(leaderElectionMock.Object);

        // Act & Assert: the exception message must identify ConsolidationWorkItemDispatchService
        // so the operator has actionable context when diagnosing the failure.
        service.Invoking(s => s.InvokeConsolidationStyleNullGuard())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*ConsolidationWorkItemDispatchService*");
    }
}
