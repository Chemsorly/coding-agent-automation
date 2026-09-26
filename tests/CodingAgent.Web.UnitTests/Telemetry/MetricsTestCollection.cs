namespace CodingAgent.Web.UnitTests.Telemetry;

// TODO [WARNING]: A duplicate [CollectionDefinition("Metrics")] already exists in
// tests/CodingAgent.Web.UnitTests/MetricsTestCollection.cs (namespace CodingAgent.Web.UnitTests).
// xUnit resolves collection definitions by name string, not namespace, so two definitions with the
// same name in one assembly are fragile: future changes to assembly scanning order or xUnit version
// upgrades could cause one definition to be silently ignored or raise a build warning.
// Remove this duplicate and keep only the original definition in MetricsTestCollection.cs.

/// <summary>
/// xUnit collection that serializes all metric tests to prevent cross-talk through the static
/// <see cref="CodingAgent.Pipeline.Telemetry.PipelineTelemetry.Meter"/> and
/// <see cref="CodingAgent.Pipeline.Telemetry.WorkDistributionTelemetry.Meter"/> singletons.
/// </summary>
[CollectionDefinition("Metrics")]
public sealed class MetricsTestCollection;
