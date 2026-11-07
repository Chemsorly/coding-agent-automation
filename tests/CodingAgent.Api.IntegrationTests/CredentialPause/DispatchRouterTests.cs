using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// Spec 056 DD2, DD7, DD12, req 3.3, 3.4, 3.7, 3.8 (task 4.9): the dispatcher routes a Pending item, pins what resolved
/// and records why it cannot start.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class DispatchRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());
    private readonly SettableTime _time = new(Now);

    private DispatchRouter Router(
        IReadOnlyList<AgentProfile>? profiles = null, IOrchestratorRunService? runs = null,
        Kubernetes.JobTemplateStore? jobTemplates = null) =>
        AgentRoutingTestSupport.Router(_db, profiles: profiles, runs: runs, time: _time, jobTemplates: jobTemplates);

    private CredentialPauseStore Pauses() =>
        new(_db, new AgentRoutingTestSupport.SemaphoreLockProvider(), _time, Serilog.Core.Logger.None);

    private Task<WorkItemEntity> SeedAsync(Action<WorkItemEntity>? configure = null, WorkItemTaskType taskType = WorkItemTaskType.Implementation) =>
        WorkItemTestData.SeedRoutedPendingAsync(_db, configure, taskType: taskType);

    private async Task<WorkItemEntity> ReadAsync(Guid id)
    {
        await using var db = _db.CreateDbContext();
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private static JobDistributionRequest Payload(WorkItemEntity entity) =>
        JsonSerializer.Deserialize<JobDistributionRequest>(entity.Payload!, PipelineJsonOptions.Lenient)!;

    [Fact]
    public async Task Routed_PinsSelectorCredentialAndRoute_AndClearsNoHold()
    {
        var item = await SeedAsync();

        var outcome = await Router().RouteAsync(item.Id, CancellationToken.None);

        outcome.Status.Should().Be(DispatchRouteStatus.Routed);
        outcome.TemplateKey.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        outcome.Credential.Should().Be(AgentCredentials.Kiro);
        var stored = await ReadAsync(item.Id);
        stored.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        stored.Credential.Should().Be(AgentCredentials.Kiro);
        stored.HoldReason.Should().BeNull();
        Payload(stored).Route!.ProfileId.Should().Be("profile-kiro");
        Payload(stored).ResolvedProfileId.Should().Be("profile-kiro");
    }

    [Fact]
    public async Task UnreadableRequest_IsHeldAsNoRoute_Req3_3()
    {
        var item = await SeedAsync(e => e.Payload = "{}");

        var outcome = await Router().RouteAsync(item.Id, CancellationToken.None);

        outcome.Status.Should().Be(DispatchRouteStatus.Held);
        outcome.HoldReason.Should().Be(HoldReasons.NoRoute);
        outcome.HoldText.Should().Be(HoldTexts.UnreadableRequest);
        var stored = await ReadAsync(item.Id);
        stored.Status.Should().Be(WorkItemStatus.Pending);
        stored.HoldReason.Should().Be(HoldReasons.NoRoute);
        stored.HoldText.Should().Be(HoldTexts.UnreadableRequest);
        stored.HeldAt.Should().Be(Now);
    }

    [Fact]
    public async Task NoRoute_IsHeld_WithTheTaskTypeAndTheFix()
    {
        var item = await SeedAsync(taskType: WorkItemTaskType.Triage);
        await AgentRoutingTestData.SeedRoutingAsync(_db, Pipeline.Models.AgentRouting.Empty);

        var outcome = await Router().RouteAsync(item.Id, CancellationToken.None);

        outcome.HoldReason.Should().Be(HoldReasons.NoRoute);
        outcome.HoldText.Should().Be("No agent route for Triage — set it in Settings → Agents → Routing");
        outcome.TemplateKey.Should().BeNull();
    }

    [Fact]
    public async Task ProfileUnavailable_IsHeld()
    {
        var item = await SeedAsync();

        var outcome = await Router(profiles: []).RouteAsync(item.Id, CancellationToken.None);

        outcome.HoldReason.Should().Be(HoldReasons.ProfileUnavailable);
        outcome.HoldText.Should().Be("The global default route for Implementation names agent profile 'profile-kiro', which does not exist");
    }

    [Fact]
    public async Task NoTool_IsHeld()
    {
        var item = await SeedAsync();

        var outcome = await Router(jobTemplates: AgentRoutingTestData.JobTemplates((AgentRoutingTestData.DefaultJobTemplate, "gemini")))
            .RouteAsync(item.Id, CancellationToken.None);

        outcome.HoldReason.Should().Be(HoldReasons.NoTool);
    }

    [Fact]
    public async Task NoTemplate_IsHeld_AndStoresTheResolvedSelector()
    {
        var item = await SeedAsync();

        var outcome = await Router(profiles: [AgentRoutingTestData.Profile(jobTemplate: "java,opencode")])
            .RouteAsync(item.Id, CancellationToken.None);

        outcome.HoldReason.Should().Be(HoldReasons.NoTemplate);
        outcome.HoldText.Should().Be("Job template java,opencode of agent profile 'profile-kiro' does not exist in the Helm values");
        (await ReadAsync(item.Id)).AgentSelector.Should().Be("java,opencode");
    }

    // DD2 (S6): a credential_paused hold stores the selector and credential the loop's skip check uses.
    [Fact]
    public async Task CredentialPaused_IsHeld_WithThePauseText_AndStoresSelectorAndCredential()
    {
        var item = await SeedAsync();
        await Pauses().PauseAsync(new AgentCredentialPause(
            AgentCredentials.Kiro, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, Now.AddDays(1), "limit"),
            CancellationToken.None);

        var outcome = await Router().RouteAsync(item.Id, CancellationToken.None);

        outcome.Status.Should().Be(DispatchRouteStatus.Held);
        outcome.HoldReason.Should().Be(HoldReasons.CredentialPaused);
        outcome.HoldText.Should().StartWith("Kiro paused — quota used up");
        outcome.Credential.Should().Be(AgentCredentials.Kiro);
        outcome.TemplateKey.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        var stored = await ReadAsync(item.Id);
        stored.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        stored.Credential.Should().Be(AgentCredentials.Kiro);
        stored.HoldReason.Should().Be(HoldReasons.CredentialPaused);
    }

    // DD7: the hold is written when it appears; repeats change nothing (HeldAt keeps the first time).
    [Fact]
    public async Task TheSameHold_ThreeTimes_IsWrittenOnce()
    {
        var item = await SeedAsync(e => e.Payload = "{}");
        var router = Router();

        await router.RouteAsync(item.Id, CancellationToken.None);
        var first = await ReadAsync(item.Id);
        _time.Now = Now.AddMinutes(5);
        await router.RouteAsync(item.Id, CancellationToken.None);
        _time.Now = Now.AddMinutes(10);
        await router.RouteAsync(item.Id, CancellationToken.None);

        var third = await ReadAsync(item.Id);
        third.HeldAt.Should().Be(first.HeldAt, "the hold did not change");
        third.HoldText.Should().Be(first.HoldText);
    }

    // req 3.8: a routing change applies to a held item at its next dispatch attempt.
    [Fact]
    public async Task AHeldItem_IsRoutedOnceTheRoutingResolves()
    {
        var item = await SeedAsync();
        await AgentRoutingTestData.SeedRoutingAsync(_db, Pipeline.Models.AgentRouting.Empty);
        var router = Router();
        (await router.RouteAsync(item.Id, CancellationToken.None)).Status.Should().Be(DispatchRouteStatus.Held);

        await AgentRoutingTestData.SeedRoutingAsync(_db);
        var outcome = await router.RouteAsync(item.Id, CancellationToken.None);

        outcome.Status.Should().Be(DispatchRouteStatus.Routed);
        (await ReadAsync(item.Id)).AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
    }

    // req 3.7: a route pinned by an earlier release (at creation) is replaced at dispatch.
    [Fact]
    public async Task ARoutePinnedByAnEarlierRelease_IsReplaced()
    {
        var item = await SeedAsync(e =>
        {
            e.AgentSelector = "old,template";
            var old = JsonSerializer.Deserialize<JobDistributionRequest>(e.Payload!, PipelineJsonOptions.Lenient)! with
            {
                AgentSelector = "old,template",
                ResolvedProfileId = "profile-old",
                Route = new AgentRouteSnapshot("profile-old", "Old", "kiro", "x", "low", AgentRouteSource.GlobalDefault),
            };
            e.Payload = JsonSerializer.Serialize(old, PipelineJsonOptions.Default);
        });

        await Router().RouteAsync(item.Id, CancellationToken.None);

        var stored = await ReadAsync(item.Id);
        stored.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        Payload(stored).Route!.ProfileId.Should().Be("profile-kiro");
    }

    // S2: a new route updates the in-memory run's profile, model and effort.
    [Fact]
    public async Task ANewRoute_UpdatesTheRunsRouteAttributes()
    {
        await AgentRoutingTestData.SeedRoutingAsync(_db, AgentRoutingTestData.Routing(model: "opus", effort: "high"));
        var item = await WorkItemTestData.SeedRoutedPendingAsync(_db, routing: AgentRoutingTestData.Routing(model: "opus", effort: "high"));
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = item.Id.ToString(),
            IssueIdentifier = new IssueIdentifier(item.IssueIdentifier),
            IssueTitle = "t",
            IssueProviderConfigId = "issue-provider",
            RepoProviderConfigId = "repo-provider",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "test",
        });
        var runs = new Mock<IOrchestratorRunService>();
        runs.Setup(r => r.GetRun(new RunId(item.Id.ToString()))).Returns(run);

        await Router(runs: runs.Object).RouteAsync(item.Id, CancellationToken.None);

        runs.Verify(r => r.ReplaceRun(It.Is<PipelineRun>(p =>
            p.AgentProfileId == "profile-kiro" && p.ModelName == "opus" && p.AgentEffort == "high")), Times.Once);
    }

    [Fact]
    public async Task NotPending_IsAnsweredAsSuch()
    {
        var item = await SeedAsync(e => e.Status = WorkItemStatus.Running);

        (await Router().RouteAsync(item.Id, CancellationToken.None)).Status.Should().Be(DispatchRouteStatus.NotPending);
        (await Router().RouteAsync(Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(DispatchRouteStatus.NotFound);
    }

    // DD12: the preview says why queued work would wait, and writes nothing.
    [Fact]
    public async Task Preview_SaysWhyItWouldWait_AndWritesNothing()
    {
        var item = await SeedAsync();
        var request = Payload(item);
        var router = Router();

        (await router.PreviewAsync(request, CancellationToken.None)).Should().BeNull("it would start");

        await Pauses().PauseAsync(new AgentCredentialPause(
            AgentCredentials.Kiro, CredentialPauseReasons.Manual, CredentialPauseSources.Manual, Now, null, "", PausedBy: "alice"),
            CancellationToken.None);
        (await router.PreviewAsync(request, CancellationToken.None)).Should()
            .Be("Kiro paused — paused by alice, since 2026-11-02 06:26 UTC");
        (await ReadAsync(item.Id)).AgentSelector.Should().BeEmpty("the preview pins nothing");
    }

    // DD7: a resume clears the credential's holds, so the items regain their place.
    [Fact]
    public async Task Resume_ClearsTheCredentialsHolds()
    {
        var item = await SeedAsync();
        var pauses = Pauses();
        await pauses.PauseAsync(new AgentCredentialPause(
            AgentCredentials.Kiro, CredentialPauseReasons.Manual, CredentialPauseSources.Manual, Now, null, ""), CancellationToken.None);
        await Router().RouteAsync(item.Id, CancellationToken.None);

        await pauses.ResumeAsync(AgentCredentials.Kiro, "bob", CancellationToken.None);

        var stored = await ReadAsync(item.Id);
        stored.HoldReason.Should().BeNull();
        stored.HoldText.Should().BeNull();
        stored.HeldAt.Should().BeNull();
        stored.Credential.Should().Be(AgentCredentials.Kiro, "the last resolution stays");
    }

    // DD7, req 3.8: saving a routing table clears every operator hold; capacity holds stay.
    [Fact]
    public async Task ClearOperatorHolds_ClearsRoutingAndPauseHolds_NotLimitHolds()
    {
        var noRoute = await SeedAsync(e => { e.HoldReason = HoldReasons.NoRoute; e.HoldText = "x"; e.HeldAt = Now; });
        var limit = await SeedAsync(e => { e.HoldReason = HoldReasons.ConcurrencyLimit; e.HoldText = "y"; e.HeldAt = Now; });

        await using (var db = _db.CreateDbContext())
            (await PendingHoldResets.ClearOperatorHoldsAsync(db, CancellationToken.None)).Should().Be(1);

        (await ReadAsync(noRoute.Id)).HoldReason.Should().BeNull();
        (await ReadAsync(limit.Id)).HoldReason.Should().Be(HoldReasons.ConcurrencyLimit);
    }

    // ── Req 3.5(a), review of spec 056 ───────────────────────────────────────────

    private static AgentCredentialPause KiroPause(DateTimeOffset? until = null) => new(
        AgentCredentials.Kiro, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, until, "quota");

    private async Task<IReadOnlyList<Guid>> DispatchWindowAsync(int size)
    {
        await using var db = _db.CreateDbContext();
        return await db.WorkItems.AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderByOperatorHoldsLast().ThenBy(w => w.CreatedAt)
            .Select(w => w.Id).Take(size).ToListAsync();
    }

    // Scenario B: items that already carry the credential (an earlier limit hold) when it gets paused all move behind
    // other work at once, so a Claude item behind 60 of them is in the first 50-item window.
    [Fact]
    public async Task APause_HoldsEveryQueuedItemOfTheCredential_AtOnce()
    {
        var kiroItems = new List<WorkItemEntity>();
        for (var i = 0; i < 60; i++)
        {
            kiroItems.Add(await SeedAsync(e =>
            {
                e.Credential = AgentCredentials.Kiro;
                e.HoldReason = HoldReasons.ConcurrencyLimit;
                e.HoldText = "4 of 4 agents are running";
                e.HeldAt = Now.AddMinutes(-5);
                e.CreatedAt = Now.AddMinutes(-60 + i);
            }));
        }
        var claude = await SeedAsync(e => { e.Credential = AgentCredentials.ClaudeSubscription; e.CreatedAt = Now; });
        var noRoute = await SeedAsync(e =>
        {
            e.Credential = AgentCredentials.Kiro;
            e.HoldReason = HoldReasons.NoRoute;
            e.HoldText = "No agent route";
            e.HeldAt = Now.AddMinutes(-5);
        });

        await Pauses().PauseAsync(KiroPause(), CancellationToken.None);

        var held = await ReadAsync(kiroItems[0].Id);
        held.HoldReason.Should().Be(HoldReasons.CredentialPaused);
        held.HoldText.Should().StartWith("Kiro paused — quota used up");
        held.HeldAt.Should().Be(Now);
        (await ReadAsync(kiroItems[59].Id)).HoldReason.Should().Be(HoldReasons.CredentialPaused);
        (await ReadAsync(claude.Id)).HoldReason.Should().BeNull("another credential is not held");
        (await ReadAsync(noRoute.Id)).HoldReason.Should().Be(HoldReasons.NoRoute, "another operator hold stays");
        (await DispatchWindowAsync(50)).Should().Contain(claude.Id);
    }

    [Fact]
    public async Task APauseThatChangesNothing_WritesNoHold_AndAnExtensionKeepsHeldAt()
    {
        var item = await SeedAsync(e => e.Credential = AgentCredentials.Kiro);
        var pauses = Pauses();
        await pauses.PauseAsync(KiroPause(Now.AddHours(1)), CancellationToken.None);
        var first = await ReadAsync(item.Id);

        _time.Now = Now.AddMinutes(10);
        await pauses.PauseAsync(KiroPause(Now.AddHours(5)), CancellationToken.None);

        var extended = await ReadAsync(item.Id);
        extended.HoldText.Should().NotBe(first.HoldText, "the text names the new end time");
        extended.HeldAt.Should().Be(first.HeldAt, "the same reason keeps its start");
    }

    // Scenario A: a routing save while the credential stays paused keeps its items waiting at the back.
    [Fact]
    public async Task ClearOperatorHolds_KeepsTheHoldsOfACredentialThatIsStillPaused()
    {
        var kiro = await SeedAsync(e =>
        {
            e.Credential = AgentCredentials.Kiro;
            e.HoldReason = HoldReasons.CredentialPaused;
            e.HoldText = "Kiro paused";
            e.HeldAt = Now;
        });
        var stale = await SeedAsync(e =>
        {
            e.Credential = AgentCredentials.Opencode;
            e.HoldReason = HoldReasons.CredentialPaused;
            e.HoldText = "OpenCode paused";
            e.HeldAt = Now;
        });

        await using (var db = _db.CreateDbContext())
            (await PendingHoldResets.ClearOperatorHoldsAsync(db, CancellationToken.None, [AgentCredentials.Kiro])).Should().Be(1);

        (await ReadAsync(kiro.Id)).HoldReason.Should().Be(HoldReasons.CredentialPaused);
        (await ReadAsync(stale.Id)).HoldReason.Should().BeNull("OpenCode is no longer paused");
    }
}
