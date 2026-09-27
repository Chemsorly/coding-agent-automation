using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using Moq;
using Octokit;
using CodingAgent.Infrastructure.GitHub;
using PipelineRateLimitExceededException = CodingAgent.Pipeline.Models.RateLimitExceededException;

namespace CodingAgent.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for <c>github.api.requests</c> counter outcome mapping.
///
/// Each test verifies that <see cref="GitHubTelemetry.ApiRequests"/> emits the correct
/// <c>operation</c> and <c>outcome</c> tags per API attempt.
///
/// Key design: the counter is emitted INSIDE the Polly retry lambda, not in the outer
/// catch blocks, so every attempt (including retried ones) increments the counter.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubApiRequestMetricsTests : IDisposable
{
    private const string TestOperation = "TestOperation";
    private readonly Mock<IGitHubClient> _mockClient;
    private readonly TestableResilienceProvider _provider;
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements;

    public GitHubApiRequestMetricsTests()
    {
        _mockClient = new Mock<IGitHubClient>();

        // Mock GetLastApiInfo() to return null so CaptureRateLimitInfo is a no-op.
#pragma warning disable CS8625 // null is valid here — Octokit's GetLastApiInfo returns null when no calls have been made
        _mockClient.Setup(c => c.GetLastApiInfo()).Returns((ApiInfo)null!);
#pragma warning restore CS8625

        _provider = new TestableResilienceProvider(_mockClient.Object);

        _measurements = new ConcurrentBag<(string, long, KeyValuePair<string, object?>[])>();
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == GitHubTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            _measurements.Add((instrument.Name, value, tags.ToArray()));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenOperationSucceeds_EmitsSuccessOutcomeOnce()
    {
        await _provider.InvokeWithResilienceAsync(_ => Task.FromResult("ok"), TestOperation, CancellationToken.None);

        var matches = ApiRequestMeasurements(TestOperation, "success");
        matches.Should().ContainSingle("counter must fire exactly once on success");
        matches[0].Value.Should().Be(1L);
    }

    [Fact]
    public async Task WhenOperationSucceeds_EmitsCorrectOperationTag()
    {
        const string op = "MySpecificOp";
        await _provider.InvokeWithResilienceAsync(_ => Task.FromResult("ok"), op, CancellationToken.None);

        var matches = ApiRequestMeasurements(op, "success");
        matches.Should().ContainSingle("counter must carry the exact operation name");
    }

    // ── Retry then success ────────────────────────────────────────────────────

    [Fact]
    public async Task WhenOperationSucceedsAfterOneRetry_EmitsTwoRequests()
    {
        // First call throws a transient error; second succeeds.
        var callCount = 0;
        await _provider.InvokeWithResilienceAsync(_ =>
        {
            callCount++;
            if (callCount == 1) throw new HttpRequestException("transient");
            return Task.FromResult("ok");
        }, TestOperation, CancellationToken.None);

        var errorMeasurements = ApiRequestMeasurements(TestOperation, "error");
        var successMeasurements = ApiRequestMeasurements(TestOperation, "success");

        errorMeasurements.Should().ContainSingle("first attempt (transient error) must emit outcome=error");
        successMeasurements.Should().ContainSingle("second attempt (success) must emit outcome=success");
    }

    // ── NotFoundException ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNotFoundException_EmitsNotFoundOutcomeAndPropagates()
    {
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
            throw new NotFoundException("Not found", HttpStatusCode.NotFound),
            TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>("NotFoundException must propagate to caller");

        var matches = ApiRequestMeasurements(TestOperation, "not_found");
        matches.Should().ContainSingle("counter must fire exactly once for not_found");
    }

    [Fact]
    public async Task WhenNotFoundException_EmitsOnlyOnce_NotRetried()
    {
        // NotFoundException is not retried by Polly — should emit exactly once
        var callCount = 0;
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
        {
            callCount++;
            throw new NotFoundException("Not found", HttpStatusCode.NotFound);
        }, TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        callCount.Should().Be(1, "NotFoundException should not be retried");

        ApiRequestMeasurements(TestOperation, "not_found").Should().ContainSingle();
        // No success or other outcomes
        ApiRequestMeasurements(TestOperation, "success").Should().BeEmpty();
    }

    // ── RateLimitExceededException ────────────────────────────────────────────

    [Fact]
    public async Task WhenRateLimitExceededException_ExhaustsRetries_EmitsRateLimitedOutcomePerAttempt()
    {
        // RateLimitExceededException is retried by Polly (1 + 3 retries = 4 total attempts).
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
            throw new Octokit.RateLimitExceededException(CreateRateLimitResponse()),
            TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<PipelineRateLimitExceededException>();

        var rateLimitedMeasurements = ApiRequestMeasurements(TestOperation, "rate_limited");
        rateLimitedMeasurements.Should().HaveCount(4,
            "RateLimitExceededException is retried 3 times (1 initial + 3 retries), each emitting rate_limited");
    }

    [Fact]
    public async Task WhenRateLimitExceededException_SingleAttemptSucceedsOnRetry_EmitsRateLimitedThenSuccess()
    {
        var callCount = 0;
        var result = await _provider.InvokeWithResilienceAsync(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new Octokit.RateLimitExceededException(CreateRateLimitResponse());
            return Task.FromResult("ok");
        }, TestOperation, CancellationToken.None);

        result.Should().Be("ok");
        ApiRequestMeasurements(TestOperation, "rate_limited").Should().ContainSingle();
        ApiRequestMeasurements(TestOperation, "success").Should().ContainSingle();
    }

    // ── AbuseException ────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenAbuseException_EmitsRateLimitedOutcome()
    {
        var callCount = 0;
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
        {
            callCount++;
            throw new AbuseException(CreateAbuseResponse());
        }, TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<PipelineRateLimitExceededException>();

        // AbuseException maps to rate_limited
        var rateLimitedMeasurements = ApiRequestMeasurements(TestOperation, "rate_limited");
        // TODO: This assertion uses NotBeEmpty rather than HaveCount(4). AbuseException is retried
        // by Polly (1 initial + 3 retries = 4 attempts), so the expected count is 4 — the same as
        // RateLimitExceededException (tested by WhenRateLimitExceededException_ExhaustsRetries_*).
        // Strengthen to .HaveCount(4, ...) to lock in the per-attempt retry counting for AbuseException.
        rateLimitedMeasurements.Should().NotBeEmpty("AbuseException must emit outcome=rate_limited");
        rateLimitedMeasurements.Should().AllSatisfy(m => m.Value.Should().Be(1L));
    }

    // ── AuthorizationException ────────────────────────────────────────────────

    [Fact]
    public async Task WhenAuthorizationException_ExhaustsRetries_EmitsFourErrorOutcomes()
    {
        // AuthorizationException is retried by Polly (1 + 3 retries = 4 total attempts),
        // confirming per-attempt counting.
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
            throw new AuthorizationException(CreateUnauthorizedResponse()),
            TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<AuthorizationException>();

        var errorMeasurements = ApiRequestMeasurements(TestOperation, "error");
        errorMeasurements.Should().HaveCount(4,
            "AuthorizationException is retried 3 times — each attempt emits outcome=error");
    }

    // ── Generic exception ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenGenericException_ExhaustsRetries_EmitsErrorOutcomes()
    {
        var act = () => _provider.InvokeWithResilienceAsync<string>(_ =>
            throw new HttpRequestException("persistent"),
            TestOperation, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();

        var errorMeasurements = ApiRequestMeasurements(TestOperation, "error");
        // TODO: This assertion uses NotBeEmpty rather than HaveCount(4). HttpRequestException is
        // retried by Polly (1 initial + 3 retries = 4 attempts), just like AuthorizationException,
        // which asserts .HaveCount(4) in WhenAuthorizationException_ExhaustsRetries_*. Strengthen
        // to .HaveCount(4, ...) to lock in the per-attempt retry counting for HttpRequestException.
        errorMeasurements.Should().NotBeEmpty("HttpRequestException must emit outcome=error");
    }

    [Fact]
    public async Task WhenGenericException_SucceedsOnRetry_EmitsErrorThenSuccess()
    {
        var callCount = 0;
        await _provider.InvokeWithResilienceAsync(_ =>
        {
            callCount++;
            if (callCount == 1) throw new HttpRequestException("transient");
            return Task.FromResult("ok");
        }, TestOperation, CancellationToken.None);

        ApiRequestMeasurements(TestOperation, "error").Should().ContainSingle();
        ApiRequestMeasurements(TestOperation, "success").Should().ContainSingle();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> ApiRequestMeasurements(
        string operation, string outcome)
        => _measurements
            .Where(m => m.Instrument == "github.api.requests"
                && m.Tags.Any(t => t.Key == "operation" && Equals(t.Value, operation))
                && m.Tags.Any(t => t.Key == "outcome" && Equals(t.Value, outcome)))
            .ToList();

    private static IResponse CreateRateLimitResponse()
    {
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(1);
        var rateLimit = new RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var mock = new Mock<IResponse>();
        mock.Setup(r => r.StatusCode).Returns(HttpStatusCode.Forbidden);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
        mock.Setup(r => r.ApiInfo).Returns(apiInfo);
        return mock.Object;
    }

    private static IResponse CreateAbuseResponse()
    {
        var mock = new Mock<IResponse>();
        mock.Setup(r => r.StatusCode).Returns(HttpStatusCode.Forbidden);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>
        {
            { "Retry-After", "5" }
        });
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
#pragma warning disable CS8625
        mock.Setup(r => r.ApiInfo).Returns((ApiInfo)null!);
#pragma warning restore CS8625
        return mock.Object;
    }

    private static IResponse CreateUnauthorizedResponse()
    {
        var mock = new Mock<IResponse>();
        mock.Setup(r => r.StatusCode).Returns(HttpStatusCode.Unauthorized);
        mock.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        mock.Setup(r => r.Body).Returns("");
        mock.Setup(r => r.ContentType).Returns("application/json");
        return mock.Object;
    }

    private sealed class TestableResilienceProvider : GitHubProviderBase
    {
        public TestableResilienceProvider(IGitHubClient client)
            : base(new GitHubConnectionInfo("https://api.github.com", "test-owner", "test-repo"), client)
        { }

        public Task<T> InvokeWithResilienceAsync<T>(
            Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct)
            => ExecuteWithResilienceAsync(operation, operationName, ct);
    }
}
