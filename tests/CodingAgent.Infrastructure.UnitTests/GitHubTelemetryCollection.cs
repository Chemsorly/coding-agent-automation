using Xunit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Serializes test classes that read or write shared static state in
/// <see cref="CodingAgent.Infrastructure.GitHub.GitHubTelemetry"/> to prevent
/// race conditions when xUnit runs test collections in parallel.
///
/// Affected classes:
/// - <see cref="GitHub.GitHubApiRequestMetricsTests"/> — subscribes to all instruments on the
///   <c>CodingAgent.GitHub</c> meter (including the rate-limit gauge).
/// - The WireMock provider tests — their successful calls write <c>_coreRateLimitRemaining</c>.
///
/// <see cref="GitHub.GitHubRateLimitGaugeTests"/> asserts on that static value, so it needs more
/// than this collection gives and runs in <see cref="GitHubRateLimitGaugeCollection"/> instead.
/// </summary>
[CollectionDefinition("GitHubTelemetry")]
public class GitHubTelemetryCollection;
