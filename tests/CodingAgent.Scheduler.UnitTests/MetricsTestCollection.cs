using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Serializes all MeterListener-based tests in this assembly to prevent cross-talk
/// through the process-global <see cref="CodingAgent.Pipeline.Telemetry.WorkDistributionTelemetry.Meter"/>
/// singleton. Any test class that creates a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// against the static meters must carry <c>[Collection("Metrics")]</c>.
/// </summary>
[CollectionDefinition("Metrics")]
public sealed class MetricsTestCollection;
