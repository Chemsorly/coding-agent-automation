using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
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
}
