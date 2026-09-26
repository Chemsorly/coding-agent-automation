using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Serializes all MeterListener-based and BackgroundService timing tests in this assembly.
///
/// <para>
/// MeterListener isolation: prevents cross-talk through the process-global
/// <see cref="CodingAgent.Pipeline.Telemetry.WorkDistributionTelemetry.Meter"/> singleton.
/// Any test class that creates a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// against the static meters must carry <c>[Collection("Metrics")]</c>.
/// </para>
///
/// <para>
/// Timing isolation: serializes <see cref="WorkItemDispatchLoop"/> and other
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> tests that use 1ms
/// <see cref="System.Threading.PeriodicTimer"/> intervals. On a loaded CI host, running
/// multiple PeriodicTimer-based services in parallel can cause
/// <c>WaitForNextTickAsync</c> to fire after <c>StopAsync</c> cancels the token, making
/// the API appear as if it was never called. Merging timing tests into this collection
/// also prevents <c>PollAndDispatchAsync</c> calls from bleeding Add(1) measurements into
/// open <see cref="System.Diagnostics.Metrics.MeterListener"/> gates in other tests.
/// </para>
/// </summary>
[CollectionDefinition("Metrics")]
public sealed class MetricsTestCollection;
