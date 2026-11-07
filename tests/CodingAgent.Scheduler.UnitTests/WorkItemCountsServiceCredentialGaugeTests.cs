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

/// <summary>Spec 056 req 8.1 (task 2.5): the <c>pipeline.agent_credential.paused</c> gauge from the Scheduler leader.</summary>
[Collection("Metrics")]
public sealed class WorkItemCountsServiceCredentialGaugeTests : IDisposable
{
    private static readonly FieldInfo CallbackField = typeof(PipelineTelemetry)
        .GetField("_agentCredentialPausedCallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("_agentCredentialPausedCallback not found — was it renamed?");

    private readonly Mock<ISchedulerApiClient> _client = new();
    private readonly Mock<ILeaderGate> _leader = new();
    private readonly Mock<ILogger> _logger = new();

    public WorkItemCountsServiceCredentialGaugeTests()
    {
        CallbackField.SetValue(null, null);
        _logger.Setup(l => l.ForContext<WorkItemCountsService>()).Returns(_logger.Object);
        _leader.SetupGet(g => g.IsLeader).Returns(true);
        _client.Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemCountsResponseDto([], null));
        _client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _client.Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CredentialPoolStatus(0, 0, 0));
        _client.Setup(c => c.GetAgentCapacityAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("capacity down"));
    }

    public void Dispose() => CallbackField.SetValue(null, null);

    private async Task<WorkItemCountsService> PollOnceAsync(Func<IReadOnlyList<AgentCredentialStatus>?> credentials)
    {
        var polled = new CallSignal();
        _client.Setup(c => c.GetAgentCredentialsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(credentials);
        var services = new ServiceCollection();
        services.AddSingleton(_client.Object);
        var svc = new WorkItemCountsService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _leader.Object, _logger.Object,
            interval: TimeSpan.FromMilliseconds(1));
        await BackgroundServiceRunner.RunUntilAsync(svc, polled.Reached, "the leader must poll the credentials");
        return svc;
    }

    [Fact]
    public async Task Paused_EmitsOnePerCredentialInUse_1WhenPaused_0WhenAvailable_EvenWhenCapacityFails()
    {
        var svc = await PollOnceAsync(() =>
        [
            new AgentCredentialStatus(AgentCredentials.Kiro, true),
            new AgentCredentialStatus(AgentCredentials.ClaudeSubscription, false),
        ]);

        var measurements = svc.CredentialPausedMeasurements()
            .Select(m => (m.Value, Credential: (string?)m.Tags.ToArray().Single(t => t.Key == "credential").Value))
            .ToList();

        measurements.Should().BeEquivalentTo([(1L, AgentCredentials.Kiro), (0L, AgentCredentials.ClaudeSubscription)]);
        CallbackField.GetValue(null).Should().NotBeNull("the service registers the gauge callback");
    }

    [Fact]
    public async Task Paused_WhenThePollFails_EmitsNothing()
    {
        var svc = await PollOnceAsync(() => throw new HttpRequestException("api down"));

        svc.CredentialPausedMeasurements().Should().BeEmpty();
    }

    [Fact]
    public async Task Paused_OnANonLeader_EmitsNothing()
    {
        var svc = await PollOnceAsync(() => [new AgentCredentialStatus(AgentCredentials.Kiro, true)]);
        _leader.SetupGet(g => g.IsLeader).Returns(false);

        svc.CredentialPausedMeasurements().Should().BeEmpty();
    }
}
