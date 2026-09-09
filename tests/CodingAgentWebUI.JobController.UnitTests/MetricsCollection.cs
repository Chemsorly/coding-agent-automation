namespace CodingAgentWebUI.JobController.UnitTests;

/// <summary>
/// xUnit collection that serialises all test classes using a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// against the static <see cref="CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.Meter"/> or
/// <see cref="CodingAgentWebUI.Pipeline.Telemetry.WorkDistributionTelemetry.MeterName"/> meters.
///
/// Because those meters are process-wide singletons, any test that emits a measurement will be
/// observed by every active MeterListener in the process. Without serialisation, a test class that
/// reads a snapshot-delta of <c>pipeline.jobs.failed</c> may pick up an extra recording fired by a
/// concurrently running test class, causing a spurious "+2 instead of +1" failure.
///
/// Tests that call <see cref="CodingAgentWebUI.JobController.Reconciliation.ReconciliationLoop.ReconcileOnceAsync"/>
/// (or any path that ultimately calls <c>PipelineTelemetry.JobsFailed.Add</c>) must be included
/// in this collection if they also listen on the static meters via MeterListener.
/// </summary>
[CollectionDefinition("Metrics")]
public class MetricsCollection;
