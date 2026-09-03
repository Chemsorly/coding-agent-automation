using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests;

/// <summary>
/// xUnit collection that serializes all metric tests within this assembly to prevent
/// cross-talk through the static <see cref="CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.Meter"/>
/// and <see cref="CodingAgentWebUI.Pipeline.Telemetry.WorkDistributionTelemetry.Meter"/> singletons.
/// </summary>
/// <remarks>
/// Without serialization, concurrent <see cref="Xunit.Sdk.MeterListener"/> instances active in
/// parallel test instances each receive every emission on the shared static meters.
/// A snapshot-delta assertion (countAfter - countBefore == 1) will fail whenever another test
/// fires between the before-snapshot and the after-snapshot of the current test.
/// </remarks>
[CollectionDefinition("Metrics")]
public class MetricsTestCollection;
