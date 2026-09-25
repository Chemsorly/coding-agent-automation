using Xunit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Serializes test classes that read or write shared static state in
/// <see cref="CodingAgent.Infrastructure.GitHub.GitHubTelemetry"/> to prevent
/// race conditions when xUnit runs test collections in parallel.
///
/// Affected classes:
/// - <see cref="GitHub.GitHubRateLimitGaugeTests"/> — writes and reads <c>_coreRateLimitRemaining</c>
///   via <c>ResetRateLimitStore()</c> / <c>CaptureRateLimitInfo()</c> and asserts gauge values
///   via <c>MeterListener.RecordObservableInstruments()</c>.
/// - <see cref="GitHub.GitHubApiRequestMetricsTests"/> — subscribes to all instruments on the
///   <c>CodingAgent.GitHub</c> meter (including the rate-limit gauge) and may trigger
///   <c>ObserveRateLimitRemaining()</c> observations that race with <c>GitHubRateLimitGaugeTests</c>.
/// </summary>
[CollectionDefinition("GitHubTelemetry")]
public class GitHubTelemetryCollection;
