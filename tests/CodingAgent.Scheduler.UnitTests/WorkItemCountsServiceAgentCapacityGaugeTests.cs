using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>Spec 057 req 7.2: the <c>workdistribution.agents.running</c> and <c>workdistribution.agents.limit</c> gauges.</summary>
[Collection("Metrics")]
public sealed class WorkItemCountsServiceAgentCapacityGaugeTests : IDisposable
{
    private static readonly FieldInfo RunningField = typeof(WorkDistributionTelemetry)
        .GetField("_agentsRunningCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentsRunningCallback not found — was it renamed?");
    private static readonly FieldInfo LimitField = typeof(WorkDistributionTelemetry)
        .GetField("_agentsLimitCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentsLimitCallback not found — was it renamed?");

    private readonly Mock<ISchedulerApiClient> _client = new();
    private readonly Mock<ILeaderGate> _leader = new();
    private readonly Mock<ILogger> _logger = new();

    public WorkItemCountsServiceAgentCapacityGaugeTests()
    {
        RunningField.SetValue(null, null);
        LimitField.SetValue(null, null);
        _logger.Setup(l => l.ForContext<WorkItemCountsService>()).Returns(_logger.Object);
        _leader.SetupGet(g => g.IsLeader).Returns(true);
        _client.Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemCountsResponseDto([new WorkItemCountDto("Running", "dotnet,kiro", 2)], null));
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _client.Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CredentialPoolStatus(0, 0, 0));
    }

    public void Dispose()
    {
        RunningField.SetValue(null, null);
        LimitField.SetValue(null, null);
    }

    private static readonly AgentCapacityStatus Status = new(
        new AgentCapacityStatusRow(null, 3, 1, 6, "helm"),
        [
            new AgentCapacityStatusRow("claude", 0, 0, null, null),
            new AgentCapacityStatusRow("dotnet,kiro", 3, 1, 3, "ui"),
        ],
        UnknownTemplateRuns: 1,
        UnknownTemplateChats: 0,
        CountsAvailable: true);

    private async Task<WorkItemCountsService> PollOnceAsync(Func<AgentCapacityStatus?> capacity)
    {
        var polled = new CallSignal();
        _client.Setup(c => c.GetAgentCapacityAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(capacity);
        var services = new ServiceCollection();
        services.AddSingleton(_client.Object);
        var svc = new WorkItemCountsService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _leader.Object, _logger.Object,
            interval: TimeSpan.FromMilliseconds(1));
        await BackgroundServiceRunner.RunUntilAsync(svc, polled.Reached, "the leader must poll the capacity");
        return svc;
    }

    private static List<(int Value, Dictionary<string, object?> Tags)> Read(IEnumerable<Measurement<int>> measurements) =>
        measurements.Select(m => (m.Value, m.Tags.ToArray().ToDictionary(t => t.Key, t => t.Value))).ToList();

    // req 7.2: zeros included per deployed template and kind; "none" only above 0.
    [Fact]
    public async Task Running_EmitsEveryTemplateAndKind_IncludingZeros()
    {
        var svc = await PollOnceAsync(() => Status);

        var running = Read(svc.RunningMeasurements());

        running.Should().HaveCount(5);
        running.Should().ContainSingle(m => (string?)m.Tags["agent_selector"] == "claude" && (string?)m.Tags["kind"] == "run" && m.Value == 0);
        running.Should().ContainSingle(m => (string?)m.Tags["agent_selector"] == "dotnet,kiro" && (string?)m.Tags["kind"] == "chat" && m.Value == 1);
        running.Should().ContainSingle(m => (string?)m.Tags["agent_selector"] == "none" && (string?)m.Tags["kind"] == "run" && m.Value == 1);
        running.Should().NotContain(m => (string?)m.Tags["agent_selector"] == "none" && (string?)m.Tags["kind"] == "chat");
    }

    // req 7.2: only rows with a limit; "all" for the global one.
    [Fact]
    public async Task Limit_EmitsOnlyRowsWithALimit_WithTheirSource()
    {
        var svc = await PollOnceAsync(() => Status);

        var limits = Read(svc.LimitMeasurements());

        limits.Should().HaveCount(2);
        limits.Should().ContainSingle(m => (string?)m.Tags["agent_selector"] == "all" && (string?)m.Tags["source"] == "helm" && m.Value == 6);
        limits.Should().ContainSingle(m => (string?)m.Tags["agent_selector"] == "dotnet,kiro" && (string?)m.Tags["source"] == "ui" && m.Value == 3);
    }

    // req 2.4: no counts → no running series (the limits still show).
    [Fact]
    public async Task Running_WhenCountsAreUnavailable_EmitsNothing()
    {
        var svc = await PollOnceAsync(() => Status with { CountsAvailable = false });

        svc.RunningMeasurements().Should().BeEmpty();
        svc.LimitMeasurements().Should().NotBeEmpty();
    }

    [Fact]
    public async Task NullBody_EmitsNothing()
    {
        var svc = await PollOnceAsync(() => null);

        svc.RunningMeasurements().Should().BeEmpty();
        svc.LimitMeasurements().Should().BeEmpty();
    }

    [Fact]
    public async Task NonLeader_EmitsNothing()
    {
        var svc = await PollOnceAsync(() => Status);
        _leader.SetupGet(g => g.IsLeader).Returns(false);

        svc.RunningMeasurements().Should().BeEmpty();
        svc.LimitMeasurements().Should().BeEmpty();
    }

    // A capacity failure resets only the agent gauges; work-item counts still poll (the #3555 note stays unchanged).
    [Fact]
    public async Task CapacityFailure_ResetsOnlyTheAgentGauges()
    {
        var polled = new CallSignal();
        _client.Setup(c => c.GetAgentCapacityAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ThrowsAsync(new HttpRequestException("api down"));
        var services = new ServiceCollection();
        services.AddSingleton(_client.Object);
        var svc = new WorkItemCountsService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _leader.Object, _logger.Object,
            interval: TimeSpan.FromMilliseconds(1));

        await BackgroundServiceRunner.RunUntilAsync(svc, polled.Reached, "the leader must poll the capacity");

        svc.RunningMeasurements().Should().BeEmpty();
        _client.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        _logger.Verify(l => l.Warning(It.IsAny<Exception>(), "WorkItemCountsService: failed to fetch counts — resetting to empty"),
            Times.Never(), "the other polls are not reset by a capacity failure");
    }

    // The callbacks reach the meter.
    [Fact]
    public async Task Gauges_AreRegisteredOnTheWorkDistributionMeter()
    {
        await PollOnceAsync(() => Status);
        var values = new List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName && instrument.Name == "workdistribution.agents.limit")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => values.Add(value));
        listener.Start();
        listener.RecordObservableInstruments();

        values.Should().BeEquivalentTo([6, 3]);
    }
}
