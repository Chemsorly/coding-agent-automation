using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// The usage threshold of the Claude subscription: a reading at the threshold pauses the credential until the window
/// resets, holds the queued work and leaves the running work alone.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class CredentialUsageMonitorTests
{
    private const string Sub = AgentCredentials.ClaudeSubscription;
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 33, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Resets = new(2026, 11, 19, 20, 20, 0, TimeSpan.Zero);

    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());
    private readonly SettableTime _time = new(Now);
    private readonly CredentialPauseStore _pauses;
    private readonly CredentialUsageStore _usage;

    public CredentialUsageMonitorTests()
    {
        var locks = new AgentRoutingTestSupport.SemaphoreLockProvider();
        _pauses = new CredentialPauseStore(_db, locks, _time, Serilog.Core.Logger.None);
        _usage = new CredentialUsageStore(_db, locks, _time);
    }

    private CredentialUsageMonitor Monitor() => new(_db, _usage, _pauses, _time, Serilog.Core.Logger.None);

    private static IReadOnlyList<AgentRateLimitObservation> FiveHour(double utilization) =>
        [new() { Provider = "claude", Window = CredentialUsageWindows.FiveHour, Status = "allowed", Utilization = utilization, ResetsAt = Resets }];

    private Task<WorkItemEntity> SeedAsync(WorkItemStatus status, string credential = Sub) =>
        WorkItemTestData.SeedRoutedPendingAsync(_db, e =>
        {
            e.Status = status;
            e.Credential = credential;
            e.AgentSelector = AgentRoutingTestData.DefaultJobTemplate;
        });

    private async Task<WorkItemEntity> ReloadAsync(Guid id)
    {
        await using var db = _db.CreateDbContext();
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    [Fact]
    public async Task WhenAReadingReachesTheThreshold_ShouldPauseUntilTheWindowResets_HoldQueuedWork_AndLeaveTheRunGoing()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        var queued = await SeedAsync(WorkItemStatus.Pending);

        await Monitor().ObserveAsync(running.Id, FiveHour(0.76), CancellationToken.None);

        var pause = await _pauses.LoadAsync(Sub, CancellationToken.None);
        pause.Should().NotBeNull();
        pause!.Reason.Should().Be(CredentialPauseReasons.UsageThreshold);
        pause.Source.Should().Be(CredentialPauseSources.Auto);
        pause.Until.Should().Be(Resets);
        pause.Detail.Should().Be("Claude 5-hour window at 76 % (threshold 75 %)");
        pause.WorkItemId.Should().Be(running.Id);

        (await ReloadAsync(running.Id)).Status.Should().Be(WorkItemStatus.Running);
        (await ReloadAsync(queued.Id)).HoldReason.Should().Be(HoldReasons.CredentialPaused);
    }

    [Fact]
    public async Task WhenTheReadingIsUnderTheThreshold_ShouldOnlyStoreIt()
    {
        var running = await SeedAsync(WorkItemStatus.Running);

        await Monitor().ObserveAsync(running.Id, FiveHour(0.74), CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None)).Should().BeNull();
        (await _usage.LoadAsync(Sub, CancellationToken.None)).Windows[CredentialUsageWindows.FiveHour].Utilization.Should().Be(0.74);
    }

    [Fact]
    public async Task WhenAChatOnTheSubscriptionReachesTheThreshold_ShouldPauseItWithoutAWorkItem()
    {
        await Monitor().ObserveCredentialAsync(Sub, FiveHour(0.8), CancellationToken.None);

        var pause = await _pauses.LoadAsync(Sub, CancellationToken.None);
        pause!.Reason.Should().Be(CredentialPauseReasons.UsageThreshold);
        pause.WorkItemId.Should().BeNull();
        pause.Until.Should().Be(Resets);
    }

    [Fact]
    public async Task WhenAChatOnTheApiKeyReportsReadings_ShouldIgnoreThem()
    {
        await Monitor().ObserveCredentialAsync(AgentCredentials.ClaudeApiKey, FiveHour(0.99), CancellationToken.None);

        (await _pauses.LoadAsync(AgentCredentials.ClaudeApiKey, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenTheRunUsesAnotherCredential_ShouldIgnoreTheReadings()
    {
        var running = await SeedAsync(WorkItemStatus.Running, AgentCredentials.ClaudeApiKey);

        await Monitor().ObserveAsync(running.Id, FiveHour(0.99), CancellationToken.None);

        (await _pauses.LoadAsync(AgentCredentials.ClaudeApiKey, CancellationToken.None)).Should().BeNull();
        (await _usage.LoadAsync(AgentCredentials.ClaudeApiKey, CancellationToken.None)).Windows.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenAnOperatorPausedIt_ShouldKeepTheManualPause()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        await _pauses.PauseAsync(new AgentCredentialPause(Sub, CredentialPauseReasons.Manual, CredentialPauseSources.Manual,
            Now, null, "maintenance", PausedBy: "adrian"), CancellationToken.None);

        await Monitor().ObserveAsync(running.Id, FiveHour(0.9), CancellationToken.None);

        var pause = await _pauses.LoadAsync(Sub, CancellationToken.None);
        pause!.Source.Should().Be(CredentialPauseSources.Manual);
        pause.Until.Should().BeNull();
    }

    [Fact]
    public async Task WhenAnOperatorResumedOverTheThreshold_ShouldNotPauseAgainUntilTheWindowResets()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        await Monitor().ObserveAsync(running.Id, FiveHour(0.8), CancellationToken.None);
        await Monitor().ResumeAsync(Sub, "adrian", CancellationToken.None);

        await Monitor().ObserveAsync(running.Id, FiveHour(0.9), CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenAnOperatorResumesWhileAReadingIsBeingChecked_ShouldKeepTheResume()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        await Monitor().ObserveAsync(running.Id, FiveHour(0.8), CancellationToken.None);
        var raced = new RacingLocks(new AgentRoutingTestSupport.SemaphoreLockProvider());
        var usage = new CredentialUsageStore(_db, raced, _time);
        var monitor = new CredentialUsageMonitor(_db, usage, _pauses, _time, Serilog.Core.Logger.None);
        // The resume lands after the reading was recorded and found over the threshold, before the pause decision.
        raced.BeforeFirstDecision = () => Monitor().ResumeAsync(Sub, "adrian", CancellationToken.None);

        await monitor.ObserveAsync(running.Id, FiveHour(0.85), CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenAThresholdIsRaisedAboveTheReading_ShouldEndTheUsageThresholdPause()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        await Monitor().ObserveAsync(running.Id, FiveHour(0.8), CancellationToken.None);
        await _usage.SaveThresholdsAsync(new CredentialUsageThresholds { FiveHourPercent = 90 }, CancellationToken.None);

        await Monitor().ReevaluateAsync("adrian", CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task WhenAThresholdIsLoweredBelowTheReading_ShouldPauseAtOnce()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        await Monitor().ObserveAsync(running.Id, FiveHour(0.6), CancellationToken.None);
        await _usage.SaveThresholdsAsync(new CredentialUsageThresholds { FiveHourPercent = 50 }, CancellationToken.None);

        await Monitor().ReevaluateAsync("adrian", CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None))!.Reason.Should().Be(CredentialPauseReasons.UsageThreshold);
    }

    [Fact]
    public async Task Reevaluate_ShouldLeaveAQuotaPauseAlone()
    {
        await _pauses.PauseAsync(new AgentCredentialPause(Sub, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto,
            Now, Resets, "session limit"), CancellationToken.None);

        await Monitor().ReevaluateAsync("adrian", CancellationToken.None);

        (await _pauses.LoadAsync(Sub, CancellationToken.None))!.Reason.Should().Be(CredentialPauseReasons.QuotaExhausted);
    }

    [Fact]
    public async Task Queue_ShouldHandTheReadingsToTheMonitorInTheBackground()
    {
        var running = await SeedAsync(WorkItemStatus.Running);
        using var queue = new CredentialUsageQueue(Monitor(), Serilog.Core.Logger.None);
        await queue.StartAsync(CancellationToken.None);

        await queue.ObserveAsync(running.Id, FiveHour(0.76), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await _pauses.LoadAsync(Sub, CancellationToken.None) is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        (await _pauses.LoadAsync(Sub, CancellationToken.None))!.Reason.Should().Be(CredentialPauseReasons.UsageThreshold);
        await queue.StopAsync(CancellationToken.None);
    }

    /// <summary>Runs <see cref="BeforeFirstDecision"/> the first time the decision lock is taken, before granting it.</summary>
    private sealed class RacingLocks(CodingAgent.Infrastructure.Locking.IDistributedLockProvider inner)
        : CodingAgent.Infrastructure.Locking.IDistributedLockProvider
    {
        public Func<Task>? BeforeFirstDecision { get; set; }

        public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        {
            if (lockName == CredentialUsageStore.DecisionLockName && BeforeFirstDecision is { } race)
            {
                BeforeFirstDecision = null;
                await race();
            }
            return await inner.AcquireAsync(lockName, ct);
        }
    }
}
