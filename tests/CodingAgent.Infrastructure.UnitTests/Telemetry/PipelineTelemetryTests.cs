using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Unit tests for <see cref="PipelineTelemetry.ExtractTraceContext"/>.
/// Covers the extraction body (L740–L744) that was previously entirely uncovered:
/// the null/empty guard path, the happy path with a valid W3C traceparent, and
/// the graceful fallback for a malformed traceparent value.
/// </summary>
/// <remarks>
/// <see cref="PipelineTelemetry.ExtractTraceContext"/> delegates to
/// <see cref="OpenTelemetry.Context.Propagation.TraceContextPropagator"/>, which is a
/// stateless W3C header parser. No OTel SDK setup or active <see cref="ActivitySource"/>
/// is required, and no <c>[Collection]</c> isolation attribute is needed.
/// </remarks>
public class PipelineTelemetryTests
{
    // ── Null / empty guard (L737–L738) ────────────────────────────────────────

    [Fact]
    public void ExtractTraceContext_NullInput_ReturnsDefaultActivityContext()
    {
        var result = PipelineTelemetry.ExtractTraceContext(null);

        result.Should().Be(default(ActivityContext));
    }

    [Fact]
    public void ExtractTraceContext_EmptyDictionary_ReturnsDefaultActivityContext()
    {
        var result = PipelineTelemetry.ExtractTraceContext(new Dictionary<string, string>());

        result.Should().Be(default(ActivityContext));
    }

    // ── Valid W3C traceparent (L740–L744, ternary true-branch) ────────────────

    /// <summary>
    /// A valid W3C traceparent causes <see cref="PipelineTelemetry.ExtractTraceContext"/>
    /// to return a non-default <see cref="ActivityContext"/> whose <c>TraceId</c> and
    /// <c>SpanId</c> match the values encoded in the header.
    /// </summary>
    [Fact]
    public void ExtractTraceContext_ValidW3CTraceparent_ReturnsNonDefaultActivityContextWithMatchingIds()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string spanId  = "00f067aa0ba902b7";
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-{spanId}-01"
        };

        var result = PipelineTelemetry.ExtractTraceContext(carrier);

        // Assert on the individual hex strings rather than the full struct so that
        // the IsRemote=true flag set by the propagator does not cause a spurious mismatch
        // with default(ActivityContext).
        result.TraceId.ToHexString().Should().Be(traceId);
        result.SpanId.ToHexString().Should().Be(spanId);
    }

    // ── Malformed traceparent (L740–L744, ternary false-branch) ──────────────

    /// <summary>
    /// A malformed traceparent value is silently ignored by the W3C propagator, causing
    /// <see cref="PipelineTelemetry.ExtractTraceContext"/> to return the default context
    /// (which causes <see cref="System.Diagnostics.ActivitySource.StartActivity"/> to
    /// create a root span rather than a child).
    /// </summary>
    [Fact]
    public void ExtractTraceContext_MalformedTraceparent_ReturnsDefaultActivityContext()
    {
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = "not-valid-traceparent"
        };

        var result = PipelineTelemetry.ExtractTraceContext(carrier);

        result.Should().Be(default(ActivityContext));
    }
}
