using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>Every agent call's subscription usage readings leave the pod while the run goes on.</summary>
public class RateLimitRelayTests
{
    private static readonly AgentRequest Request = new() { Prompt = "p", WorkspacePath = "/ws" };

    private static readonly IReadOnlyList<AgentRateLimitObservation> Readings =
    [
        new() { Provider = "claude", Window = "five_hour", Status = "allowed", Utilization = 0.42 },
        new() { Provider = "claude", Window = "seven_day", Status = "allowed", Utilization = 0.17 },
    ];

    private static ProviderFailureRecordingAgentProvider Decorated(ProviderFailureRecorder recorder, AgentUsageDetails? usage)
    {
        var inner = new Mock<IAgentProvider>();
        inner.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [], UsageDetails = usage });
        return new ProviderFailureRecordingAgentProvider(inner.Object, recorder);
    }

    [Fact]
    public async Task WhenAnAgentCallReportsReadings_ShouldRelayThemToTheJobSink()
    {
        var recorder = new ProviderFailureRecorder();
        var relayed = new List<IReadOnlyList<AgentRateLimitObservation>>();
        using var relay = recorder.RelayRateLimits(relayed.Add);

        await Decorated(recorder, new AgentUsageDetails { RateLimits = Readings }).ExecuteAsync(Request, CancellationToken.None);

        relayed.Should().ContainSingle().Which.Should().BeEquivalentTo(Readings);
    }

    [Fact]
    public async Task WhenTheSameReadingsComeAgain_ShouldRelayThemOnce()
    {
        var recorder = new ProviderFailureRecorder();
        var relayed = 0;
        using var relay = recorder.RelayRateLimits(_ => relayed++);
        var provider = Decorated(recorder, new AgentUsageDetails { RateLimits = Readings });

        await provider.ExecuteAsync(Request, CancellationToken.None);
        await provider.ExecuteAsync(Request, CancellationToken.None);

        relayed.Should().Be(1);
    }

    [Fact]
    public void WhenTheProviderReportsWhileACallRuns_ShouldRelayEachNewReadingAtOnce()
    {
        var recorder = new ProviderFailureRecorder();
        var relayed = new List<IReadOnlyList<AgentRateLimitObservation>>();
        using var relay = recorder.RelayRateLimits(relayed.Add);
        var inner = new Mock<IAgentProvider>();
        var reporting = inner.As<IReportsRateLimits>();
        reporting.SetupProperty(r => r.RateLimitsObserved);

        _ = new ProviderFailureRecordingAgentProvider(inner.Object, recorder);
        reporting.Object.RateLimitsObserved!(Readings);

        relayed.Should().ContainSingle().Which.Should().BeEquivalentTo(Readings);
    }

    [Fact]
    public async Task WhenTheCallReportsNoReadings_ShouldRelayNothing()
    {
        var recorder = new ProviderFailureRecorder();
        var relayed = 0;
        using var relay = recorder.RelayRateLimits(_ => relayed++);

        await Decorated(recorder, usage: null).ExecuteAsync(Request, CancellationToken.None);
        await Decorated(recorder, new AgentUsageDetails()).ExecuteAsync(Request, CancellationToken.None);

        relayed.Should().Be(0);
    }

    [Fact]
    public async Task AfterTheRelayIsDisposed_ShouldRelayNothing()
    {
        var recorder = new ProviderFailureRecorder();
        var relayed = 0;
        recorder.RelayRateLimits(_ => relayed++).Dispose();

        await Decorated(recorder, new AgentUsageDetails { RateLimits = Readings }).ExecuteAsync(Request, CancellationToken.None);

        relayed.Should().Be(0);
    }

    [Theory]
    [InlineData(null, AgentCredentials.ClaudeSubscription)]
    [InlineData("subscription", AgentCredentials.ClaudeSubscription)]
    [InlineData("apiKey", AgentCredentials.ClaudeApiKey)]
    public void ChatCredential_ShouldFollowTheAuthModeThePromptCarries(string? authMode, string expected) =>
        ChatJobExecutor.ChatCredential(new ChatPromptMessage { SessionId = "s1", Prompt = "p", AgentAuthMode = authMode })
            .Should().Be(expected);

    [Fact]
    public async Task WhenTheSinkThrows_ShouldStillReturnTheResultOfTheCall()
    {
        var recorder = new ProviderFailureRecorder();
        using var relay = recorder.RelayRateLimits(_ => throw new InvalidOperationException("hub gone"));

        var result = await Decorated(recorder, new AgentUsageDetails { RateLimits = Readings }).ExecuteAsync(Request, CancellationToken.None);

        result.ExitCode.Should().Be(0);
    }
}
