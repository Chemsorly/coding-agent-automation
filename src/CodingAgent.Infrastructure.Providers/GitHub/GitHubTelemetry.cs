using System.Diagnostics.Metrics;
using CodingAgent.Pipeline.Telemetry;

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

    // TODO: The Meter instance is static and is never reset between test classes. If PreInitialize()
    // is called in one test class, subsequent tests using a MeterListener on this meter will observe
    // the pre-seeded zero-value measurements. Only ResetRateLimitStore() currently resets static
    // state. If test isolation issues arise, consider adding a ResetCounterState() helper (or
    // guarding PreInitialize() with a flag) so test constructors can fully reset GitHubTelemetry.
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
    /// Must NOT be pre-initialized — doing so would permanently emit a 0 value from boot,
    /// violating the "only emit after a call" requirement.
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

    // ── Operation name constants ──────────────────────────────────────────────

    /// <summary>
    /// All GitHub API operation names passed to <c>ExecuteWithResilienceAsync</c>.
    /// Used for pre-initializing <c>github.api.requests</c> counter tag combinations at process
    /// startup so that rare outcomes (e.g. <c>rate_limited</c>) appear as zero-value series
    /// before the first real increment, enabling Prometheus <c>increase()</c> to work correctly.
    /// </summary>
    // TODO: There is no compile-time enforcement tying the entries below to the actual operationName
    // strings passed at each ExecuteWithResilienceAsync call site. A rename at a call site silently
    // desynchs this list: the pre-initialized zero-value series exists under the old name while real
    // increments go to the new (unregistered) name, causing Prometheus increase() to miss the first
    // increment. To detect drift, consider a test that reflects over all ExecuteWithResilienceAsync
    // call sites (e.g. via Roslyn or a source-file grep) and asserts each operationName literal
    // appears in AllOperationNames.
    public static readonly string[] AllOperationNames =
    [
        // GitHubProviderBase
        "ValidateRepository",

        // GitHubRepositoryProvider.PullRequests.cs
        "IsPullRequestBehindBase",
        "ListAgentBranches",
        "DeleteBranch",
        "CreatePullRequest",
        "GetAgentPullRequests.Search",
        "GetAgentPullRequests.Get",
        "GetAgentPullRequests.ReviewComments",
        "GetAgentPullRequests.ConversationComments",
        "GetAgentPullRequests.Reviews",
        "ListOpenPullRequests",
        "ListOpenPullRequests.GetDetail",
        "AddPrLabel",
        "RemovePrLabel",
        "ExtractLinkedIssues.Timeline",
        "ExtractLinkedIssues.GetPr",
        "ListPrComments.IssueComments",
        "ListPrComments.ReviewComments",
        "ListPrComments.Reviews",
        "UpdatePullRequest",
        "GetPullRequestForDraftCheck",
        "GetPullRequestForDraftConversion",
        "GetPullRequestBody",
        "ClosePullRequest",
        "GetPullRequestMergedState",

        // GitHubRepositoryProvider.Reviews.cs
        "SubmitPullRequestReview",
        "SubmitPullRequestReviewWithComments",
        "DismissPreviousReview.GetAllReviews",
        "DismissPreviousReview.Dismiss",
        "FindExistingReviewComment",
        "UpdateReviewComment",

        // GitHubRepositoryProvider.Commits.cs
        "GetCommitCountSince",

        // GitHubIssueProvider.cs
        "GetIssue",
        "ListOpenIssues",
        "ListClosedIssues",
        "PostComment",
        "UpdateComment",
        "ListComments",
        "AddLabels",
        "RemoveLabel",
        "ListRepositoryLabels",
        "HasAgentLabels",
        "EnsureAgentLabels",
        "IsIssueClosed",
        "CloseIssue",
        "CreateIssue",

        // GitHubActionsPipelineProvider.cs
        "GetRunStatus.ListRuns",
        "GetRunStatus.ListJobs",
    ];

    /// <summary>Closed set of outcome tag values for <c>github.api.requests</c>.</summary>
    public static readonly string[] OutcomeValues = ["success", "not_found", "rate_limited", "error"];

    /// <summary>Closed set of outcome tag values for <c>pipeline.pull_requests.closed</c>.</summary>
    public static readonly string[] PrOutcomeValues = ["merged", "closed_unmerged"];

    /// <summary>
    /// Pre-initializes all counter tag combinations by emitting <c>Add(0)</c> for:
    /// <list type="bullet">
    ///   <item><description>
    ///     Each <c>(operation × outcome)</c> pair for <c>github.api.requests</c>.
    ///   </description></item>
    ///   <item><description>
    ///     Each <c>outcome</c> value for <c>pipeline.pull_requests.closed</c>
    ///     (<see cref="PrOutcomeValues"/>: <c>merged</c>, <c>closed_unmerged</c>).
    ///   </description></item>
    /// </list>
    ///
    /// Call this at process startup after the OTel meter is registered (after <c>app.Build()</c>).
    /// This ensures Prometheus <c>increase()</c> can detect the first real increment — without
    /// pre-initialization, a series that has never been incremented does not exist in the
    /// TSDB and <c>increase()</c> over the first window returns no data.
    ///
    /// The <c>github.rate_limit.remaining</c> ObservableGauge is intentionally excluded:
    /// pre-initializing it would permanently emit a 0 value from boot, violating the
    /// "only emit after a call" requirement.
    /// </summary>
    public static void PreInitialize()
    {
        // Seed github.api.requests: every (operation × outcome) combination.
        foreach (var operation in AllOperationNames)
        {
            foreach (var outcome in OutcomeValues)
            {
                ApiRequests.Add(0,
                    new KeyValuePair<string, object?>("operation", operation),
                    new KeyValuePair<string, object?>("outcome", outcome));
            }
        }

        // Seed pipeline.pull_requests.closed: every outcome value.
        // PullRequestsClosed lives on PipelineTelemetry.Meter (registered in the same processes).
        // Without this, the first increment of a rare outcome (e.g. closed_unmerged on a fresh
        // replica) is invisible to Prometheus increase() because the series does not yet exist.
        foreach (var outcome in PrOutcomeValues)
        {
            PipelineTelemetry.PullRequestsClosed.Add(0,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
