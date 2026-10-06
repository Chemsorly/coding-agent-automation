using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Services;

public class DatabaseStartupServiceTests
{
    private readonly Mock<IDbContextFactory<PipelineDbContext>> _dbFactoryMock = new();
    private readonly Mock<IDistributedLockProvider> _lockProviderMock = new();
    private readonly Serilog.ILogger _logger = new LoggerConfiguration().CreateLogger();

    private DatabaseStartupService CreateService(
        IDatabaseProbe probe,
        Dictionary<string, string?>? configValues = null,
        TimeProvider? timeProvider = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        return new DatabaseStartupService(
            _dbFactoryMock.Object, _lockProviderMock.Object, config, _logger, probe, timeProvider);
    }

    #region Connection Retry Tests

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_SucceedsOnFirstAttempt()
    {
        var probe = new FakeProbe(failCount: 0);
        var service = CreateService(probe);

        await service.WaitForDatabaseConnectionAsync(CancellationToken.None);

        Assert.Equal(1, probe.AttemptCount);
    }

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_RetriesOnTransientFailure_ThenSucceeds()
    {
        // Fail 1 time, succeed on 2nd — keeps test fast (~2s delay)
        var probe = new FakeProbe(failCount: 1);
        var service = CreateService(probe);

        await service.WaitForDatabaseConnectionAsync(CancellationToken.None);

        Assert.Equal(2, probe.AttemptCount);
    }

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_CancellationAbortsDuringRetry()
    {
        // Always fails; the retry delay runs on fake time, so it never elapses on its own and the
        // cancellation below always lands during the delay after the first failure. (A real 500ms
        // CTS armed before the call fired before the first probe whenever the test thread stalled.)
        var fakeTime = new FakeTimeProvider();
        var probe = new FakeProbe(failCount: 100);
        var service = CreateService(probe, timeProvider: fakeTime);
        using var cts = new CancellationTokenSource();

        var task = service.WaitForDatabaseConnectionAsync(cts.Token);
        Assert.False(task.IsCompleted, "Task should be waiting on the retry delay");

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // Exactly 1 attempt was made before cancellation
        Assert.Equal(1, probe.AttemptCount);
    }

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_OperationCanceledException_PropagatesImmediately()
    {
        // The probe throws OperationCanceledException — should not retry
        var probe = new CancellingProbe();
        var service = CreateService(probe);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.WaitForDatabaseConnectionAsync(CancellationToken.None));

        Assert.Equal(1, probe.AttemptCount);
    }

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_AllRetriesExhausted_ThrowsInvalidOperationException()
    {
        // Validates the "all retries exhausted" exception wrapping path. Every attempt fails; the
        // exponential backoff is driven by FakeTimeProvider so no real wall-clock time is spent
        // (previously this waited ~20s bounded by a cancellation token).
        var fakeTime = new FakeTimeProvider();
        var probe = new FakeProbe(failCount: DatabaseStartupService.MaxRetryAttempts);
        var service = CreateService(probe, timeProvider: fakeTime);

        var task = service.WaitForDatabaseConnectionAsync(CancellationToken.None);

        // Pump fake time past each retry backoff until the retry sequence exhausts. The tiny real
        // yield lets the awaiting continuation observe each advance; an advance that lands before
        // the next delay is scheduled is simply repeated on the next iteration. The real deadline
        // is only a safety net against an unexpected hang (the loop normally finishes in well under
        // a second), and the task is checked before awaiting it: once the pumping stops, the
        // remaining fake delays never elapse and awaiting the task would hang the test.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            fakeTime.Advance(DatabaseStartupService.MaxDelay);
        }

        Assert.True(task.IsCompleted, "the retry sequence must exhaust once fake time passes every backoff");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Contains($"after {DatabaseStartupService.MaxRetryAttempts} attempts", caught.Message);
        Assert.Equal(DatabaseStartupService.MaxRetryAttempts, probe.AttemptCount);
    }

    [Fact]
    public void RetryConstants_MatchSpecification()
    {
        // Spec: 2s → 30s, max 10 attempts
        Assert.Equal(TimeSpan.FromSeconds(2), DatabaseStartupService.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), DatabaseStartupService.MaxDelay);
        Assert.Equal(10, DatabaseStartupService.MaxRetryAttempts);
    }

    [Fact]
    public async Task WaitForDatabaseConnectionAsync_FirstRetryDelay_IsApproximately2Seconds()
    {
        // Verify the first retry delay uses InitialDelay (2s) via FakeTimeProvider — no wall-clock dependency
        var fakeTime = new FakeTimeProvider();
        var probe = new FakeProbe(failCount: 1);
        var service = CreateService(probe, timeProvider: fakeTime);

        var task = service.WaitForDatabaseConnectionAsync(CancellationToken.None);

        // Task should be waiting on the 2s delay — not yet completed
        Assert.False(task.IsCompleted, "Task should be waiting on delay");

        // Advance time by exactly InitialDelay (2s) — should unblock
        fakeTime.Advance(DatabaseStartupService.InitialDelay);
        await task;

        // Probe should have been called twice: first fail, then success after delay
        Assert.Equal(2, probe.AttemptCount);
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Fake probe that fails a configurable number of times then succeeds.
    /// </summary>
    private sealed class FakeProbe : IDatabaseProbe
    {
        private readonly int _failCount;
        public int AttemptCount { get; private set; }

        public FakeProbe(int failCount) => _failCount = failCount;

        public Task ProbeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AttemptCount++;
            if (AttemptCount <= _failCount)
                throw new Npgsql.NpgsqlException("Connection refused");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Probe that always throws OperationCanceledException.
    /// </summary>
    private sealed class CancellingProbe : IDatabaseProbe
    {
        public int AttemptCount { get; private set; }

        public Task ProbeAsync(CancellationToken ct)
        {
            AttemptCount++;
            throw new OperationCanceledException();
        }
    }

    #endregion
}
