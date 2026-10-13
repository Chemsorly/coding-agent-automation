using System.Diagnostics.Metrics;

namespace CodingAgent.Infrastructure.GitHub;

/// <summary>
/// Telemetry instruments for GitHub API interactions.
/// Defined on a dedicated meter (<c>"CodingAgent.GitHub"</c>) so that agent pods — which must
/// not record these metrics — can simply omit <c>AddMeter("CodingAgent.GitHub")</c> from
/// their OTel configuration, causing the OTel SDK to silently discard all measurements.
/// Long-lived processes (API, Scheduler, Web, JobController) register this meter explicitly.
/// </summary>
public static class GitHubTelemetry
{
    /// <summary>Meter name registered in the OTel configuration of each long-lived process.</summary>
    public const string MeterName = "CodingAgent.GitHub";

    // TODO: Meter implements IDisposable but is never disposed here. In production this is benign
    // (process-lifetime static), but the live Meter instance and its registered instruments
    // accumulate across all test classes sharing the same AppDomain. ResetRateLimitStore() resets
    // field values but cannot recreate instruments on the existing Meter. If full in-process
    // isolation is required for a future test, the Meter would need to be replaced with a new
    // instance, which is not currently supported by this static design.
    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Counts GitHub API request attempts with outcome tagging.
    /// Tags: <c>operation</c> (provider method name), <c>outcome</c> (success | not_found | rate_limited | error).
    /// Emitted per attempt — including retries — so retried errors contribute multiple increments.
    /// Only exported from processes that register <c>AddMeter("CodingAgent.GitHub")</c>.
    /// </summary>
    public static readonly Counter<long> ApiRequests = Meter.CreateCounter<long>(
        "github.api.requests",
        unit: "{request}",
        description: "GitHub API request attempt outcomes. Emitted per attempt including retries.");

    // ── Rate-limit gauge store ────────────────────────────────────────────────

    // Volatile long fields store the most recently observed rate-limit remaining values.
    // -1 means "no observation yet" — the gauge emits no measurement in that case.
    // Written on every successful (or failed) API call inside the Polly lambda before
    // the transient GitHubClient is discarded.
    // Volatile reads/writes are sufficient here: individual long reads/writes are atomic
    // on 64-bit platforms, and torn reads on 32-bit platforms would at worst emit a stale
    // (not garbage) value — acceptable for a gauge that approximates rate-limit headroom.
    private static long _coreRateLimitRemaining = -1;
    private static long _graphqlRateLimitRemaining = -1;

    /// <summary>
    /// Observable gauge for remaining GitHub API rate-limit quota.
    /// Tags: <c>resource</c> (core | graphql).
    /// Emits no measurement until at least one API call has been made in this process.
    /// </summary>
    public static readonly ObservableGauge<double> RateLimitRemaining =
        Meter.CreateObservableGauge<double>(
            "github.rate_limit.remaining",
            observeValues: ObserveRateLimitRemaining,
            unit: "{request}",
            description: "Remaining GitHub API rate-limit quota by resource (core or graphql). " +
                         "Only emitted from processes that have made at least one GitHub API call.");

    /// <summary>
    /// Updates the stored rate-limit remaining value for the given resource.
    /// Called from <see cref="GitHubProviderBase"/> after each API call, inside the Polly lambda,
    /// before the transient <see cref="Octokit.IGitHubClient"/> is discarded.
    /// </summary>
    /// <param name="resource"><c>"core"</c> for REST API calls; <c>"graphql"</c> for GraphQL mutations.</param>
    /// <param name="remaining">The remaining rate-limit value from <c>GetLastApiInfo().RateLimit.Remaining</c>.</param>
    internal static void UpdateRateLimit(string resource, long remaining)
    {
        if (resource == "core")
            Volatile.Write(ref _coreRateLimitRemaining, remaining);
        else if (resource == "graphql")
            Volatile.Write(ref _graphqlRateLimitRemaining, remaining);
    }

    /// <summary>Resets the rate-limit store to initial state. Used in tests to isolate test runs.</summary>
    internal static void ResetRateLimitStore()
    {
        Volatile.Write(ref _coreRateLimitRemaining, -1);
        Volatile.Write(ref _graphqlRateLimitRemaining, -1);
    }

    private static IEnumerable<Measurement<double>> ObserveRateLimitRemaining()
    {
        var core = Volatile.Read(ref _coreRateLimitRemaining);
        var graphql = Volatile.Read(ref _graphqlRateLimitRemaining);

        if (core >= 0)
            yield return new Measurement<double>(
                (double)core,
                new KeyValuePair<string, object?>("resource", "core"));

        if (graphql >= 0)
            yield return new Measurement<double>(
                (double)graphql,
                new KeyValuePair<string, object?>("resource", "graphql"));
    }
}
