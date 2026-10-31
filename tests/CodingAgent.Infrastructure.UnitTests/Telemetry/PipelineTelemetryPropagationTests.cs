using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="PipelineTelemetry.CaptureTraceContext"/> and
/// <see cref="PipelineTelemetry.FormatTraceParent"/>.
/// These methods require an <see cref="ActivityListener"/> to observe spans;
/// the listener is created and disposed per test class via <see cref="IDisposable"/>.
/// </summary>
public class PipelineTelemetryPropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public PipelineTelemetryPropagationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── CaptureTraceContext — input validation ────────────────────────────────

    [Fact]
    public void CaptureTraceContext_NullActivityName_ThrowsArgumentException()
    {
        var act = () => PipelineTelemetry.CaptureTraceContext(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CaptureTraceContext_EmptyActivityName_ThrowsArgumentException()
    {
        var act = () => PipelineTelemetry.CaptureTraceContext(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    // ── CaptureTraceContext — no listener (null-span fast path) ──────────────

    /// <summary>
    /// When no <see cref="ActivityListener"/> is registered for the pipeline source,
    /// <see cref="ActivitySource.StartActivity"/> returns null and
    /// <see cref="PipelineTelemetry.CaptureTraceContext"/> returns null.
    /// </summary>
    [Fact]
    public void CaptureTraceContext_NoListener_ReturnsNull()
    {
        // Detach the listener for this test to exercise the no-listener branch.
        _listener.Dispose();

        var result = PipelineTelemetry.CaptureTraceContext("Test.Capture");

        result.Should().BeNull();
    }

    // ── CaptureTraceContext — ambient Activity.Current path ──────────────────

    /// <summary>
    /// When an ambient <see cref="Activity.Current"/> exists, <see cref="PipelineTelemetry.CaptureTraceContext"/>
    /// injects the ambient context into the carrier without creating a new span.
    /// The returned traceparent must encode the ambient activity's trace ID and span ID.
    /// </summary>
    [Fact]
    public void CaptureTraceContext_WithAmbientActivity_ReturnsCarrierEncodingAmbientContext()
    {
        using var ambient = PipelineTelemetry.ActivitySource.StartActivity("Test.AmbientParent");
        ambient.Should().NotBeNull("a listener is registered");

        var result = PipelineTelemetry.CaptureTraceContext("Test.Capture");

        result.Should().NotBeNull();
        result!.Should().ContainKey("traceparent");
        var traceparent = result["traceparent"];
        traceparent.Should().Contain(ambient!.TraceId.ToHexString(),
            "the returned carrier must encode the ambient activity's trace ID");
        traceparent.Should().Contain(ambient.SpanId.ToHexString(),
            "the returned carrier must encode the ambient activity's span ID");
    }

    // ── CaptureTraceContext — fallback span creation path ────────────────────

    /// <summary>
    /// When no ambient <see cref="Activity.Current"/> exists but a listener is registered,
    /// <see cref="PipelineTelemetry.CaptureTraceContext"/> mints a short-lived Producer span,
    /// injects its context, and returns a non-null carrier containing a valid W3C traceparent.
    /// </summary>
    [Fact]
    public void CaptureTraceContext_NoAmbientActivity_WithListener_ReturnsCarrierWithNewContext()
    {
        // Ensure no ambient activity is active for this call.
        Activity.Current.Should().BeNull("this test must run without an active activity");

        var result = PipelineTelemetry.CaptureTraceContext("Test.CaptureNoAmbient");

        result.Should().NotBeNull();
        result!.Should().ContainKey("traceparent");
        // A valid W3C traceparent starts with "00-" followed by a 32-hex trace ID.
        result["traceparent"].Should().MatchRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$",
            "the generated traceparent must conform to the W3C spec");
    }

    // ── FormatTraceParent ────────────────────────────────────────────────────

    [Fact]
    public void FormatTraceParent_NullActivity_ReturnsNull()
    {
        var result = PipelineTelemetry.FormatTraceParent(null);

        result.Should().BeNull();
    }

    /// <summary>
    /// For a live <see cref="Activity"/>, <see cref="PipelineTelemetry.FormatTraceParent"/>
    /// must return a W3C-compliant traceparent that encodes the activity's own trace ID and span ID.
    /// </summary>
    [Fact]
    public void FormatTraceParent_ValidActivity_ReturnsW3CTraceparentEncodingActivityContext()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test.FormatParent");
        activity.Should().NotBeNull("a listener is registered");

        var result = PipelineTelemetry.FormatTraceParent(activity);

        result.Should().NotBeNull();
        result.Should().Contain(activity!.TraceId.ToHexString(),
            "traceparent must encode the activity's trace ID");
        result.Should().Contain(activity.SpanId.ToHexString(),
            "traceparent must encode the activity's span ID");
        result.Should().MatchRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$",
            "traceparent must conform to the W3C spec");
    }
}
