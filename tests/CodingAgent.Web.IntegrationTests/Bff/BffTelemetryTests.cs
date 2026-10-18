using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.Bff;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>
/// Spec 052 Req 13.3, design V17: the noise filter drops a polling call's server span when the
/// request starts, so the forwarder's client span is not recorded either (parent-based sampling) and
/// the API gets a <c>traceparent</c> without the sampled flag, so it records nothing for the call.
/// </summary>
[Collection("SmokeTests")]
public class BffTelemetryTests : IClassFixture<BffWebApplicationFactory>
{
    private readonly BffWebApplicationFactory _factory;

    public BffTelemetryTests(BffWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PollingCall_IsForwardedUnsampled_AndRecordsNoSpan()
    {
        var (traceparent, spans) = await ForwardAndObserveAsync("/api/ui/health");

        traceparent.Should().EndWith("-00", "the API must not record a span for a poll");
        spans.Should().NotBeEmpty("positive control: the listener saw the call's spans");
        spans.Should().OnlyContain(s => !s.Recorded, "no span of a poll may be exported");
    }

    [Fact]
    public async Task OtherCall_IsForwardedSampled_AndRecordsTheServerAndForwarderSpans()
    {
        var (traceparent, spans) = await ForwardAndObserveAsync("/api/ui/runs");

        traceparent.Should().EndWith("-01");
        spans.Should().Contain(s => s.Recorded && s.Kind == ActivityKind.Server, "the web host's server span");
        spans.Should().Contain(s => s.Recorded && s.Kind == ActivityKind.Client, "the forwarder's client span");
    }

    /// <summary>
    /// Forwards one GET and returns the <c>traceparent</c> the stub API received, plus every finished
    /// ASP.NET Core and HttpClient span the web host created in that trace. The listener does not vote
    /// on sampling, so the host's own OpenTelemetry sampler and noise filter decide what is recorded.
    /// </summary>
    private async Task<(string Traceparent, IReadOnlyList<Activity> Spans)> ForwardAndObserveAsync(string path)
    {
        using var client = await _factory.SignInClientAsync("reader");
        // The test host builds the tracer provider on first use; its listener must come before ours,
        // so its noise processor has run when our ActivityStopped sees the span.
        _ = _factory.Services.GetRequiredService<TracerProvider>();
        var finished = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            ActivityStopped = finished.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var before = _factory.Api.Requests.Count;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(BffRequestGuard.CsrfHeader, "1");
        var response = await client.Http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var upstream = _factory.Api.Requests.Skip(before).Should().ContainSingle().Subject;
        var traceparent = upstream.Headers.Should().ContainKey("traceparent").WhoseValue;
        var traceId = traceparent.Split('-')[1];
        // The server span stops after the response is sent; wait for it briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!finished.Any(a => a.TraceId.ToHexString() == traceId && a.Kind == ActivityKind.Server)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        // The trace's root is this test's own call to the web host (the browser's place); every other
        // span of the trace is the web host's.
        return (traceparent, finished.Where(a => a.TraceId.ToHexString() == traceId && a.ParentSpanId != default).ToList());
    }
}
