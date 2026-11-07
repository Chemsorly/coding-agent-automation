using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// The credential pause tests record <c>pipeline.agent_credential.*</c> for fixed credential names, so they run apart
/// from every other test (a parallel test's pause would show in a listener).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CredentialPauseCollection
{
    public const string Name = "CredentialPauseCollection";
}

/// <summary>A clock a test sets.</summary>
public sealed class SettableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Captures the credential pause counters while it lives.</summary>
public sealed class CredentialMetrics : IDisposable
{
    private readonly MeterListener _listener = new();

    public CredentialMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName && instrument.Name.StartsWith("pipeline.agent_credential.", StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (Recorded)
                Recorded.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())));
        });
        _listener.Start();
    }

    public List<(string Name, long Value, Dictionary<string, string?> Tags)> Recorded { get; } = [];

    public int Count(string name, string credential, string? source = null) =>
        Recorded.Count(r => r.Name == name && r.Tags.GetValueOrDefault("credential") == credential
            && (source is null || r.Tags.GetValueOrDefault("source") == source));

    public void Dispose() => _listener.Dispose();
}

/// <summary>Spec 056 DD5, req 2.1–2.3, 2.5, 8.1, 9.4 (task 2.1, 2.6): the pause store through EF InMemory.</summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class CredentialPauseStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);
    private const string Pauses = "pipeline.agent_credential.pauses";
    private const string Resumes = "pipeline.agent_credential.resumes";

    private readonly string _db = Guid.NewGuid().ToString();
    private readonly SettableTime _time = new(Now);
    private readonly List<LogEvent> _logs = [];

    private CredentialPauseStore Store() => new(
        new AgentRoutingTestSupport.InMemoryFactory(_db), new AgentRoutingTestSupport.SemaphoreLockProvider(), _time,
        AgentRoutingTestSupport.Logger(_logs));

    private static AgentCredentialPause Auto(DateTimeOffset? until, string detail = "monthly usage limit") => new(
        AgentCredentials.Kiro, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, until, detail,
        WorkItemId: Guid.NewGuid(), RunId: "run-1");

    private static AgentCredentialPause Manual(DateTimeOffset? until = null) => new(
        AgentCredentials.Kiro, CredentialPauseReasons.Manual, CredentialPauseSources.Manual, Now, until, "note", PausedBy: "alice");

    [Fact]
    public async Task NoEntry_IsAvailable_Req2_1()
    {
        var store = Store();

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
        (await store.LoadAllAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pause_IsStoredUnderItsOwnKey_AndRecordedOnce_Req2_2_8_1()
    {
        using var metrics = new CredentialMetrics();
        var store = Store();

        var stored = await store.PauseAsync(Auto(Now.AddDays(3)), CancellationToken.None);

        stored.Should().NotBeNull();
        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().Be(stored);
        await using var db = AgentRoutingTestSupport.InMemoryFactory.Create(_db);
        (await db.KeyValueStore.Select(kv => kv.Key).ToListAsync()).Should().Equal("agent-credential-pause:kiro");
        metrics.Count(Pauses, AgentCredentials.Kiro, CredentialPauseSources.Auto).Should().Be(1);
        _logs.Should().Contain(e => e.Level == LogEventLevel.Information
            && e.Properties.ContainsKey("Credential") && e.Properties.ContainsKey("PauseReason")
            && e.Properties.ContainsKey("PauseSource") && e.Properties.ContainsKey("PausedUntil"));
    }

    [Fact]
    public async Task AutoOverManual_ChangesNothing_AndRecordsNoPause_Req2_5()
    {
        var store = Store();
        await store.PauseAsync(Manual(), CancellationToken.None);
        using var metrics = new CredentialMetrics();

        (await store.PauseAsync(Auto(Now.AddDays(1)), CancellationToken.None)).Should().BeNull();

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None))!.Source.Should().Be(CredentialPauseSources.Manual);
        metrics.Count(Pauses, AgentCredentials.Kiro).Should().Be(0);
    }

    [Fact]
    public async Task ManualOverAuto_Replaces_ButIsNoNewPauseInTheCounter_Req2_5_8_1()
    {
        var store = Store();
        await store.PauseAsync(Auto(Now.AddDays(1)), CancellationToken.None);
        using var metrics = new CredentialMetrics();

        await store.PauseAsync(Manual(), CancellationToken.None);

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None))!.PausedBy.Should().Be("alice");
        metrics.Count(Pauses, AgentCredentials.Kiro).Should().Be(0, "the counter counts available → paused only");
    }

    [Fact]
    public async Task AutoOverAuto_ExtendsOnlyToALaterUntil_Req2_5()
    {
        var store = Store();
        await store.PauseAsync(Auto(Now.AddDays(5), "first"), CancellationToken.None);

        (await store.PauseAsync(Auto(Now.AddDays(1)), CancellationToken.None)).Should().BeNull();
        var extended = await store.PauseAsync(Auto(Now.AddDays(9)), CancellationToken.None);

        extended!.Until.Should().Be(Now.AddDays(9));
        extended.Detail.Should().Be("first");
    }

    [Fact]
    public async Task Resume_DeletesTheEntry_AndRecordsAManualResume()
    {
        var store = Store();
        await store.PauseAsync(Manual(), CancellationToken.None);
        using var metrics = new CredentialMetrics();

        (await store.ResumeAsync(AgentCredentials.Kiro, "bob", CancellationToken.None)).Should().BeTrue();
        (await store.ResumeAsync(AgentCredentials.Kiro, "bob", CancellationToken.None)).Should().BeFalse("it is not paused any more");

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
        metrics.Count(Resumes, AgentCredentials.Kiro, CredentialPauseSources.Manual).Should().Be(1);
    }

    [Fact]
    public async Task AnExpiredPause_IsAvailable_AndResumedOnceAsReset_Req2_3()
    {
        var store = Store();
        await store.PauseAsync(Auto(Now.AddMinutes(10)), CancellationToken.None);
        _time.Now = Now.AddMinutes(11);
        using var metrics = new CredentialMetrics();

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
        (await store.LoadAllAsync(CancellationToken.None)).Should().BeEmpty();
        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();

        metrics.Count(Resumes, AgentCredentials.Kiro, CredentialPauseSources.Reset).Should().Be(1);
    }

    [Fact]
    public async Task Expire_BeforeTheEndTime_ChangesNothing()
    {
        var store = Store();
        await store.PauseAsync(Auto(Now.AddMinutes(10)), CancellationToken.None);

        (await store.ExpireAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeFalse();

        (await store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().NotBeNull();
    }

    // T4 canary (req 9.4, 9.5): a detail with a fake key reaches neither the stored entry nor the log unmasked.
    [Fact]
    public async Task Detail_WithAFakeKey_IsRedactedInTheEntryAndTheLog_T4()
    {
        const string fakeKey = "sk-ant-api03-CANARYcanaryCANARYcanary0000";
        const string fakeToken = "ghp_CANARYcanaryCANARYcanary000000";
        var store = Store();

        var stored = await store.PauseAsync(
            Auto(null, $"billing_error: key {fakeKey} rejected\napi_key={fakeToken}"), CancellationToken.None);

        stored!.Detail.Should().NotContain(fakeKey).And.NotContain(fakeToken).And.NotContain("\n");
        await using var db = AgentRoutingTestSupport.InMemoryFactory.Create(_db);
        (await db.KeyValueStore.SingleAsync()).Value.Should().NotContain(fakeKey).And.NotContain(fakeToken);
        _logs.Select(e => e.RenderMessage()).Should().NotContain(m => m.Contains(fakeKey) || m.Contains(fakeToken));
    }

    [Fact]
    public async Task Detail_IsCutTo300Characters_Req2_2()
    {
        var stored = await Store().PauseAsync(Auto(null, new string('x', 400)), CancellationToken.None);

        stored!.Detail.Should().HaveLength(AgentCredentialPause.MaxDetailLength);
    }
}
