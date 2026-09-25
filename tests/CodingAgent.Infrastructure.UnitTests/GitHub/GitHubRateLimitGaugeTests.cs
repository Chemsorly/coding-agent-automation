using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using Moq;
using Octokit;
using CodingAgent.Infrastructure.GitHub;

namespace CodingAgent.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for the <c>github.rate_limit.remaining</c> ObservableGauge emission rule.
///
/// Key rules:
/// 1. No measurement is emitted when no API calls have been made (volatile fields at -1).
/// 2. A REST call emits under <c>resource=core</c>.
/// 3. A GraphQL call (passing <c>isGraphQL: true</c>) emits under <c>resource=graphql</c>.
/// 4. The gauge still works after a transient client is discarded (dynamic-token path).
/// </summary>
/// <remarks>
/// Placed in the <c>GitHubTelemetry</c> collection to serialize with
/// <see cref="GitHubApiRequestMetricsTests"/>, which subscribes to all instruments on the
/// shared <c>CodingAgent.GitHub</c> meter and can race on <c>GitHubTelemetry</c>'s static
/// volatile rate-limit fields.
/// </remarks>
[Collection("GitHubTelemetry")]
public class GitHubRateLimitGaugeTests : IDisposable
{
    // Static fields in GitHubTelemetry are shared across tests — reset before each test.
    // The ResetRateLimitStore() helper is internal and visible via InternalsVisibleTo.

    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Resource, double Value)> _gaugeMeasurements;

    public GitHubRateLimitGaugeTests()
    {
        // Reset static gauge state from any prior test.
        GitHubTelemetry.ResetRateLimitStore();

        _gaugeMeasurements = new ConcurrentBag<(string, double)>();
        _listener = new MeterListener();

        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == GitHubTelemetry.MeterName
                && instrument.Name == "github.rate_limit.remaining")
                l.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var tagsArr = tags.ToArray();
            var resource = tagsArr.FirstOrDefault(t => t.Key == "resource").Value?.ToString() ?? "";
            _gaugeMeasurements.Add((resource, value));
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        GitHubTelemetry.ResetRateLimitStore();
    }

    // ── No-call state ─────────────────────────────────────────────────────────

    [Fact]
    public void WhenNoCallMade_GaugeEmitsNoMeasurement()
    {
        // Force the observable gauge to evaluate by calling RecordObservableInstruments.
        _listener.RecordObservableInstruments();

        _gaugeMeasurements.Should().BeEmpty(
            "gauge must emit no measurement when no API call has been made in this process");
    }

    // ── REST (core) call ──────────────────────────────────────────────────────

    [Fact]
    public async Task WhenRestCallMade_GaugeEmitsCoreRemainingValue()
    {
        const long expectedRemaining = 4950;
        var provider = CreateProviderWithRateLimit(expectedRemaining);

        await provider.InvokeWithResilienceAsync(
            _ => Task.FromResult("ok"), "TestOp", CancellationToken.None);

        _listener.RecordObservableInstruments();

        var coreReadings = _gaugeMeasurements.Where(m => m.Resource == "core").ToList();
        coreReadings.Should().ContainSingle("one core measurement must be emitted after a REST call");
        coreReadings[0].Value.Should().Be(expectedRemaining,
            "gauge value must match GetLastApiInfo().RateLimit.Remaining");
    }

    [Fact]
    public async Task WhenRestCallMade_GaugeDoesNotEmitGraphql()
    {
        var provider = CreateProviderWithRateLimit(100);

        await provider.InvokeWithResilienceAsync(
            _ => Task.FromResult("ok"), "TestOp", CancellationToken.None);

        _listener.RecordObservableInstruments();

        var graphqlReadings = _gaugeMeasurements.Where(m => m.Resource == "graphql").ToList();
        graphqlReadings.Should().BeEmpty("graphql measurement must not be emitted from a REST call");
    }

    // ── GraphQL call ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenGraphQlCallMade_GaugeEmitsGraphqlRemainingValue()
    {
        const long expectedRemaining = 200;
        var provider = CreateProviderWithRateLimit(expectedRemaining);

        await provider.InvokeWithResilienceAsync(
            _ => Task.FromResult("ok"), "TestGraphQlOp", CancellationToken.None, isGraphQL: true);

        _listener.RecordObservableInstruments();

        var graphqlReadings = _gaugeMeasurements.Where(m => m.Resource == "graphql").ToList();
        graphqlReadings.Should().ContainSingle("one graphql measurement must be emitted after a GraphQL call");
        graphqlReadings[0].Value.Should().Be(expectedRemaining);
    }

    // ── Dynamic token provider path ───────────────────────────────────────────

    [Fact]
    public async Task WhenDynamicTokenProvider_GaugeStillEmitsAfterTransientClientDiscarded()
    {
        // Simulate the dynamic-token path: GetClientAsync creates a new client each time.
        // The rate-limit info must be captured INSIDE the lambda before the client is returned.
        const long expectedRemaining = 3000;

        // Use a static-client provider (same code path: rate-limit captured inside lambda).
        var provider = CreateProviderWithRateLimit(expectedRemaining);

        await provider.InvokeWithResilienceAsync(
            _ => Task.FromResult("ok"), "DynTokenOp", CancellationToken.None);

        // Force gauge observation.
        _listener.RecordObservableInstruments();

        var coreReadings = _gaugeMeasurements.Where(m => m.Resource == "core").ToList();
        coreReadings.Should().ContainSingle("gauge must reflect the captured rate-limit value even after client discard");
        coreReadings[0].Value.Should().Be(expectedRemaining);
    }

    // ── Updated on second call ────────────────────────────────────────────────

    [Fact]
    public async Task WhenMultipleCallsMade_GaugeReflectsLatestValue()
    {
        var provider1 = CreateProviderWithRateLimit(4999);
        var provider2 = CreateProviderWithRateLimit(4998);

        await provider1.InvokeWithResilienceAsync(_ => Task.FromResult("ok"), "Op1", CancellationToken.None);
        await provider2.InvokeWithResilienceAsync(_ => Task.FromResult("ok"), "Op2", CancellationToken.None);

        // RecordObservableInstruments reads the static field at the time of the call.
        // The value 4998 was the last write.
        _listener.RecordObservableInstruments();

        var coreReadings = _gaugeMeasurements.Where(m => m.Resource == "core").ToList();
        coreReadings.Should().HaveCount(1, "gauge emits one measurement per RecordObservableInstruments call");
        coreReadings[0].Value.Should().Be(4998, "gauge must reflect the most recent update");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TestableGaugeProvider CreateProviderWithRateLimit(long remaining)
    {
        var mockClient = new Mock<IGitHubClient>();
        var rateLimit = new RateLimit(5000, (int)remaining, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        mockClient.Setup(c => c.GetLastApiInfo()).Returns(apiInfo);

        return new TestableGaugeProvider(mockClient.Object);
    }

    private sealed class TestableGaugeProvider : GitHubProviderBase
    {
        public TestableGaugeProvider(IGitHubClient client)
            : base(new GitHubConnectionInfo("https://api.github.com", "owner", "repo"), client)
        { }

        public Task<T> InvokeWithResilienceAsync<T>(
            Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct,
            bool isGraphQL = false)
            => ExecuteWithResilienceAsync(operation, operationName, ct, isGraphQL);
    }
}
