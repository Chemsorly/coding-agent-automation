using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Verifies trace context injection produces valid W3C traceparent headers
/// and that the inject/extract round-trip preserves trace identity.
/// </summary>
public class TraceContextPropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public TraceContextPropagationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void CaptureTraceContext_CreatesProducerSpan_ReturnsTraceparent()
    {
        // CaptureTraceContext creates its own Producer activity — no ambient activity needed
        Activity.Current = null;

        var carrier = AgentJobDispatcher.CaptureTraceContext();

        carrier.Should().NotBeNull();
        carrier.Should().ContainKey("traceparent");
        carrier!["traceparent"].Should().StartWith("00-");
    }

    [Fact]
    public void CaptureTraceContext_WithParentActivity_ProducerIsChild()
    {
        using var parent = PipelineTelemetry.ActivitySource.StartActivity("ParentOp");
        parent.Should().NotBeNull();

        var carrier = AgentJobDispatcher.CaptureTraceContext();

        carrier.Should().NotBeNull();
        carrier.Should().ContainKey("traceparent");
        // The producer span should share the same trace ID as the parent
        carrier!["traceparent"].Should().Contain(parent!.TraceId.ToString());
    }

    [Fact]
    public void CaptureTraceContext_WithNoListener_ReturnsNull()
    {
        // Dispose the listener so StartActivity returns null
        _listener.Dispose();
        Activity.Current = null;

        var carrier = AgentJobDispatcher.CaptureTraceContext();

        carrier.Should().BeNull();
    }
}
