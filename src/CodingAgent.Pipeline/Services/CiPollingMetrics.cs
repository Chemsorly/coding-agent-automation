using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Packages the four telemetry instruments needed by <see cref="CiPollingCoordinator"/>,
/// avoiding a verbose constructor signature while keeping the instruments as a cohesive unit.
/// </summary>
internal sealed record CiPollingMetrics(
    Histogram<double> ExternalCiDuration,
    Histogram<double> PostPrCiDuration,
    Histogram<double> StepDuration,
    Counter<long> StepCount);
