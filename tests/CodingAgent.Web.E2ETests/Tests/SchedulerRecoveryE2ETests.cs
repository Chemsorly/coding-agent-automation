using System.Net.Http.Json;
using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests for scheduler recovery behaviour:
/// <list type="bullet">
///   <item><see cref="WhenTheConfigIsUnreadable_ShouldLeaveTheLoop_AndApplyTheStartAfterRecovery"/> —
///   verifies that <see cref="LoopWatchdogService"/> silently swallows a config-read failure
///   and self-heals once the API is reachable again.</item>
///   <item><see cref="WhenAFailedRunEndedRecently_ShouldKeepTheLabel_UntilTheCooldownHasPassed"/> —
///   verifies that <see cref="OrphanedLabelRecoveryService"/> skips an issue whose WorkItem
///   completed within the dedup window, and recovers it once the window passes.</item>
///   <item><see cref="WhenStartedAsAHostedService_ShouldWaitForTheGracePeriod_ThenSweep"/> —
///   verifies that <c>ExecuteAsync</c> waits the configured grace period before running the
///   first sweep.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SchedulerRecoveryE2ETests : HeadlessE2ETestBase
{
    public SchedulerRecoveryE2ETests(E2EFixture fixture) : base(fixture) { }

    // ── S4 — Watchdog recovers after config failure ───────────────────────

    /// <summary>
    /// S4: when the Scheduler cannot read its config, LoopWatchdogService silently skips the
    /// tick without starting the loop. Once the config is readable again, the hosted 250ms timer
    /// picks up <c>ClosedLoopAutoStart = true</c> and starts the loop autonomously.
    ///
    /// <para>
    /// <c>FailPipelineConfigLoad</c> lives on the Scheduler host's own
    /// <see cref="InMemoryPipelineApiConfigClient"/>; it is not cleared by
    /// <c>ResetAll</c>, so this test clears it in a <c>finally</c> block.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenTheConfigIsUnreadable_ShouldLeaveTheLoop_AndApplyTheStartAfterRecovery()
    {
        // Arrange: seed a template and a route so the watchdog can start the loop
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "watchdog-recovery-template",
            Name = "Watchdog Recovery Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "watchdog-recovery-profile",
            DisplayName = "Watchdog Recovery Profile",
            JobTemplate = E2ERouting.Key("e2e"),
            Enabled = true,
        });

        // Resolve the Scheduler host's InMemoryPipelineApiConfigClient and break config loading
        var schedulerConfigClient = (InMemoryPipelineApiConfigClient)
            Fixture.SchedulerFactory.Services.GetRequiredService<IPipelineApiConfigClient>();
        schedulerConfigClient.FailPipelineConfigLoad = true;

        // Write ClosedLoopAutoStart through the non-failing config store path so the value is
        // persisted, but the watchdog cannot read it until the flag is cleared
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { ClosedLoopAutoStart = true }, CancellationToken.None);

        try
        {
            // Act 1: tick while config is unreadable — must not throw
            await Fixture.SchedulerFactory.Services.GetRequiredService<LoopWatchdogService>()
                .TickAsync(CancellationToken.None);

            // Assert 1: loop was not started
            Assert.False(Fixture.SchedulerFactory.LoopService.IsLoopActive,
                "Watchdog must not start the loop when config read fails");

            // Act 2: restore config read; let the hosted 250ms timer pick up ClosedLoopAutoStart = true
            schedulerConfigClient.FailPipelineConfigLoad = false;

            await WaitUntilAsync(
                () => Fixture.SchedulerFactory.LoopService.IsLoopActive,
                timeout: TimeSpan.FromSeconds(10));

            // Assert 2: loop status is also reflected at the API endpoint
            using var apiClient = Fixture.CreateUiApiClient();
            await WaitUntilAsync(async () =>
            {
                var status = await apiClient.GetFromJsonAsync<UiLoopStatus>(
                    "/api/ui/loop/status",
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return status?.IsLoopActive == true;
            }, timeout: TimeSpan.FromSeconds(10));
        }
        finally
        {
            schedulerConfigClient.FailPipelineConfigLoad = false;
            await Fixture.StopLoopAsync();
        }
    }

    // ── S7 — Orphan recovery respects the dedup cooldown window ──────────

    /// <summary>
    /// S7a: an <c>agent:in-progress</c> issue whose WorkItem completed within the 5-minute
    /// dedup cooldown is NOT recovered (the label stays). Once the WorkItem's
    /// <c>CompletedAt</c> moves outside the window, the next sweep removes the label.
    /// </summary>
    [Fact]
    public async Task WhenAFailedRunEndedRecently_ShouldKeepTheLabel_UntilTheCooldownHasPassed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // Arrange: a template so OrphanedLabelRecoveryService discovers "issue-e2e" to scan
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "orphan-dedup-template",
            Name = "Orphan Dedup Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, ct);

        // Issue 9301 has agent:in-progress; we will insert a WorkItem for it
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9301",
            Title = "Issue #9301",
            Description = string.Empty,
            Labels = [AgentLabels.InProgress],
        });

        // Insert as Pending, then flip to Failed with CompletedAt = UtcNow (within dedup window)
        await InsertPendingWorkItemAsync("9301");

        await using var db1 = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db1.WorkItems.SingleAsync(w => w.IssueIdentifier == "9301", ct);
        entity.Status = WorkItemStatus.Failed;
        entity.CompletedAt = DateTimeOffset.UtcNow;
        entity.FailureReason = FailureReason.AgentError;
        await db1.SaveChangesAsync(ct);

        // Act 1: sweep while the WorkItem is within the cooldown window
        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // Assert 1: no label changes for 9301 — recent completion protects it
        var changes1 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "9301")
            .ToList();
        Assert.Empty(changes1);

        // Move CompletedAt to 1 hour ago — outside the 5-minute dedup cooldown
        await using var db2 = Fixture.DbContextFactory.CreateDbContext();
        var entity2 = await db2.WorkItems.SingleAsync(w => w.IssueIdentifier == "9301", ct);
        entity2.CompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db2.SaveChangesAsync(ct);

        // Act 2: sweep again — now the cooldown has passed
        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // Assert 2: agent:in-progress removed and agent:error added
        var adds9301 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "9301" && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        var removes9301 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "9301" && !lc.Added)
            .Select(lc => lc.Label)
            .ToList();

        Assert.Contains(AgentLabels.Error, adds9301);
        Assert.Contains(AgentLabels.InProgress, removes9301);
    }

    // ── S7 — Orphan recovery grace period ────────────────────────────────

    /// <summary>
    /// S7b: when <see cref="OrphanedLabelRecoveryService"/> is started as a hosted service, its
    /// <c>ExecuteAsync</c> waits the configured grace period before running the first sweep.
    /// An immediately-following assertion confirms no label changes have occurred; after the grace
    /// period expires, the sweep runs and the orphaned label is corrected.
    /// </summary>
    [Fact]
    public async Task WhenStartedAsAHostedService_ShouldWaitForTheGracePeriod_ThenSweep()
    {
        // Arrange: template and an issue with agent:in-progress but no WorkItem
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "orphan-grace-template",
            Name = "Orphan Grace Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9302",
            Title = "Issue #9302",
            Description = string.Empty,
            Labels = [AgentLabels.InProgress],
        });

        // Build a new OrphanedLabelRecoveryService instance with a short 3-second grace period.
        // All dependencies come from the Scheduler host's DI container (not the API host).
        // leaderGate: null — acts as leader unconditionally, matching how the harness registers it.
        // Serilog.Log.Logger is Serilog.ILogger, matching the constructor's parameter type.
        var service = new OrphanedLabelRecoveryService(
            Fixture.SchedulerFactory.Services.GetRequiredService<IRunActivityQuery>(),
            Fixture.SchedulerFactory.Services.GetRequiredService<IServiceScopeFactory>(),
            Fixture.SchedulerFactory.Services.GetRequiredService<IProviderFactory>(),
            Fixture.SchedulerFactory.Services.GetRequiredService<ILabelService>(),
            leaderGate: null,
            Serilog.Log.Logger,
            gracePeriod: TimeSpan.FromSeconds(3));

        try
        {
            // Act: start the service — ExecuteAsync begins its grace period delay asynchronously
            await service.StartAsync(CancellationToken.None);

            // Assert immediately: no sweep has happened yet
            var immediate = Fixture.IssueProvider.LabelChanges
                .Where(lc => lc.Identifier == "9302")
                .ToList();
            Assert.Empty(immediate);

            // Wait for the sweep to run after the grace period (20s covers the 3s grace + sweep time)
            await WaitUntilAsync(
                () => Fixture.IssueProvider.LabelChanges
                    .Any(lc => lc.Identifier == "9302" && lc.Added && lc.Label == AgentLabels.Error),
                timeout: TimeSpan.FromSeconds(20));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
