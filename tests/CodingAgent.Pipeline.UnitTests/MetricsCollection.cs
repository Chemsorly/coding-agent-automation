/// <summary>
/// xUnit collection that serialises all test classes that listen on the process-global static
/// <see cref="CodingAgent.Pipeline.Telemetry.PipelineTelemetry.Meter"/> via
/// <see cref="System.Diagnostics.Metrics.MeterListener"/>.
///
/// Because the meter is a process-wide singleton, any measurement emitted by one test class will
/// be observed by every active <c>MeterListener</c> in the process. Without serialisation, a test
/// that expects exactly one increment may capture an extra recording fired by a concurrently
/// running test, producing spurious "2 items instead of 1" failures.
/// </summary>
[CollectionDefinition("Metrics")]
public class MetricsCollection;
