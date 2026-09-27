using Xunit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Runs <see cref="GitHub.GitHubRateLimitGaugeTests"/> on its own, after all parallel collections.
///
/// The gauge reads the static rate-limit fields in
/// <see cref="CodingAgent.Infrastructure.GitHub.GitHubTelemetry"/>, and every successful
/// <c>GitHubProviderBase</c> call writes them, in any test class. The WireMock provider tests are
/// the usual writers: their stubs send no <c>X-RateLimit-*</c> headers, so Octokit reports
/// <c>Remaining = 0</c>. A shared collection serializes only the classes that opt in, and
/// <c>GitHubRepositoryProviderExtractLinkedIssuesFailureTests</c> cannot, because it needs the
/// StaticLogger collection. A parallel write replaced the value a gauge test had just written
/// (CI: "expected 4998.0 ... but found 0.0").
/// </summary>
[CollectionDefinition("GitHubRateLimitGauge", DisableParallelization = true)]
public class GitHubRateLimitGaugeCollection;
