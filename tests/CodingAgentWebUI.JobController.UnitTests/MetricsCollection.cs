using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests;

/// <summary>
/// Serializes test classes that use a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// against the process-wide <c>PipelineTelemetry</c> and <c>WorkDistributionTelemetry</c> meters.
/// Without this collection, parallel test class instances share the same static meters, causing
/// stray recordings to bleed into snapshot-delta assertions in sibling tests.
/// </summary>
[CollectionDefinition("Metrics")]
public class MetricsCollection;
