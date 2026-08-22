namespace CodingAgentWebUI.UnitTests.Telemetry;

/// <summary>
/// xUnit collection that serializes all metric tests to prevent cross-talk through the static
/// <see cref="CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.Meter"/> and
/// <see cref="CodingAgentWebUI.Pipeline.Telemetry.WorkDistributionTelemetry.Meter"/> singletons.
/// </summary>
[CollectionDefinition("Metrics")]
public sealed class MetricsTestCollection;
