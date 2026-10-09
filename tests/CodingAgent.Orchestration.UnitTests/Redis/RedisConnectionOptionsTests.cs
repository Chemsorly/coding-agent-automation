using AwesomeAssertions;
using CodingAgent.Orchestration.Redis;
using StackExchange.Redis;

namespace CodingAgent.Orchestration.UnitTests.Redis;

/// <summary>
/// Unit tests for <see cref="RedisConnectionOptions.Parse"/>.
///
/// These are pure unit tests — no network calls are made. They verify that
/// the resilience defaults are applied correctly and that <c>abortConnect=true</c>
/// in the connection string is unconditionally overridden to <c>false</c>.
/// </summary>
public class RedisConnectionOptionsTests
{
    [Fact]
    public void Parse_SetsResilientDefaultsWithoutChannelPrefix()
    {
        // Act
        var opts = RedisConnectionOptions.Parse("redis:6379");

        // Assert — AbortOnConnectFail must be false so startup does not crash when Redis is unreachable
        opts.AbortOnConnectFail.Should().BeFalse(
            "pods must start with a disconnected multiplexer rather than throw during service registration");

        // Assert — ConnectRetry must be 5
        opts.ConnectRetry.Should().Be(5,
            "5 initial connection retries before giving up and returning a disconnected multiplexer");

        // Assert — ReconnectRetryPolicy must be ExponentialRetry
        opts.ReconnectRetryPolicy.Should().BeOfType<ExponentialRetry>(
            "reconnects must use exponential back-off to avoid thundering-herd on Redis recovery");

        // Assert — no channelPrefix set; callers that need it (SignalR backplane) set it themselves
        // TODO: This assertion is fragile — it relies on ConfigurationOptions.ToString() not including
        // the word "channelPrefix" when the property is at its default value. If StackExchange.Redis
        // changes its serialization format in a future version, the test could silently pass even when
        // ChannelPrefix is set, or fail when it is correctly unset. Prefer a direct property assertion:
        //   opts.ChannelPrefix.Should().Be(default(RedisChannel), "Parse must not set a channel prefix");
        // (Review finding: TestQualityReviewer WARNING RedisConnectionOptionsTests.cs:35)
        opts.ToString().Should().NotContain("channelPrefix",
            "RedisConnectionOptions.Parse must not set a channel prefix — callers set it themselves");
    }

    [Fact]
    public void Parse_OverridesAbortConnectTrue()
    {
        // Arrange — connection string explicitly requests abortConnect=true
        // Act
        var opts = RedisConnectionOptions.Parse("redis:6379,abortConnect=true");

        // Assert — our override wins; the pod must not crash during startup
        opts.AbortOnConnectFail.Should().BeFalse(
            "abortConnect=true in the connection string must be overridden to false by RedisConnectionOptions.Parse");
    }
}
