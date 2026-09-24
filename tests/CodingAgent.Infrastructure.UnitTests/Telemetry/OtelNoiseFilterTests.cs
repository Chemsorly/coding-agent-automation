using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Telemetry;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="OtelNoiseFilter"/> filter predicates, span-name enrichment, and the
/// <see cref="OtelNoiseSpanDropProcessor"/>.
/// </summary>
/// <remarks>
/// The HttpClient filter tests manipulate the <c>KUBERNETES_SERVICE_HOST</c> environment variable.
/// The class implements <see cref="IDisposable"/> to restore it after each test, and uses
/// <c>[Collection("EnvironmentVariables")]</c> to serialise execution against other env-var tests.
/// </remarks>
[Collection("EnvironmentVariables")]
public class OtelNoiseFilterTests : IDisposable
{
    private readonly string? _originalK8sHost;

    public OtelNoiseFilterTests()
    {
        _originalK8sHost = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", _originalK8sHost);
    }

    // ── FilterAspNetCoreRequest ────────────────────────────────────────────────

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/loop/status")]
    public void FilterAspNetCoreRequest_NoisePaths_ReturnsFalse(string path)
    {
        var context = BuildHttpContext(path);
        OtelNoiseFilter.FilterAspNetCoreRequest(context).Should().BeFalse(
            because: $"'{path}' is a noise path that should be dropped");
    }

    [Theory]
    [InlineData("/api/work-items/")]
    [InlineData("/api/agents/")]
    [InlineData("/hubs/agent")]
    [InlineData("/healthz/detail")]     // sub-path of a noise path — must be kept
    [InlineData("/loop/status/full")]   // sub-path of a noise path — must be kept
    [InlineData("/readyz/extra")]       // sub-path of a noise path — must be kept
    [InlineData("/")]
    public void FilterAspNetCoreRequest_NonNoisePaths_ReturnsTrue(string path)
    {
        var context = BuildHttpContext(path);
        OtelNoiseFilter.FilterAspNetCoreRequest(context).Should().BeTrue(
            because: $"'{path}' is not a noise path and must be kept");
    }

    [Fact]
    public void FilterAspNetCoreRequest_NullPath_ReturnsTrue()
    {
        // TODO: [WARNING] This test passes string.Empty, not null. PathString("").Value is "" (not null),
        // so the explicit `if (path is null) return true` guard is never exercised here — it returns true
        // via the pattern-match fallthrough instead. To truly cover the null branch, construct a
        // DefaultHttpContext without setting Request.Path (leaving it as PathString.Empty whose .Value IS null).
        // See TestQualityReviewer finding at OtelNoiseFilterTests.cs:60.
        // Build a context with empty path to cover defensive branch.
        var context = BuildHttpContext(string.Empty);
        OtelNoiseFilter.FilterAspNetCoreRequest(context).Should().BeTrue();
    }

    /// <summary>
    /// Property: any path that is not exactly one of the three noise paths is kept.
    /// </summary>
    [Property]
    public Property FilterAspNetCoreRequest_AnyPathOtherThanNoiseIsKept()
    {
        var noisePaths = new HashSet<string> { "/healthz", "/readyz", "/loop/status" };

        // TODO: [WARNING] Gen.Elements cycles over only 7 fixed strings — FsCheck provides no real randomised
        // coverage. A regression dropping an arbitrary path would never be caught. Replace with
        // Arb.Default.NonEmptyString() (or similar) filtered to exclude the three exact noise paths, so the
        // invariant is verified against genuinely random input. See TestQualityReviewer finding at
        // OtelNoiseFilterTests.cs:83.
        var gen = Gen.Elements("/api/x", "/dashboard", "/healthz/detail", "/other", "/loop/statuses",
            "/readyz/probe", "/loop/status/extra");

        return Prop.ForAll(gen.ToArbitrary(), path =>
        {
            if (noisePaths.Contains(path)) return true; // excluded from this property
            return OtelNoiseFilter.FilterAspNetCoreRequest(BuildHttpContext(path));
        });
    }

    // ── FilterHttpClientRequest ────────────────────────────────────────────────

    [Fact]
    public void FilterHttpClientRequest_WhenHostMatchesK8sServiceHost_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", "10.43.0.1");
        var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "http://10.43.0.1/apis/coordination.k8s.io/v1/namespaces/default/leases");

        OtelNoiseFilter.FilterHttpClientRequest(request).Should().BeFalse(
            because: "requests to the Kubernetes API server (KUBERNETES_SERVICE_HOST) must be dropped");
    }

    [Fact]
    public void FilterHttpClientRequest_WhenHostIsKubernetesDefaultSvc_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);
        var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "https://kubernetes.default.svc/api/v1/namespaces");

        OtelNoiseFilter.FilterHttpClientRequest(request).Should().BeFalse(
            because: "'kubernetes.default.svc' is the well-known K8s DNS fallback and must be dropped");
    }

    [Theory]
    [InlineData("https://api.github.com/repos/owner/repo")]
    [InlineData("https://coding-agent-api.svc.cluster.local/api/work-items")]
    [InlineData("http://internal-service:8080/health")]
    public void FilterHttpClientRequest_NonK8sRequests_ReturnsTrue(string url)
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);

        OtelNoiseFilter.FilterHttpClientRequest(request).Should().BeTrue(
            because: $"'{url}' is not a Kubernetes API server request and must be kept");
    }

    // TODO: [WARNING] Missing test: KUBERNETES_SERVICE_HOST is set to a concrete IP (e.g. "10.43.0.1")
    // but the request host does NOT match it (e.g. request goes to "api.github.com"). The current tests
    // only cover the case where the env var is null or the host matches. A regression that dropped any
    // non-matching host while the env var is set would not be caught. Add a test asserting that
    // FilterHttpClientRequest returns true when the env var is set but the request host differs.
    // See TestQualityReviewer finding at OtelNoiseFilterTests.cs:108.

    // TODO: [WARNING] Missing test: case-insensitive matching for the "kubernetes.default.svc" fallback.
    // Production code uses StringComparison.OrdinalIgnoreCase for this comparison. A test passing
    // "KUBERNETES.DEFAULT.SVC" or "Kubernetes.Default.Svc" as the host would verify this, and its absence
    // means a regression dropping the OrdinalIgnoreCase flag to Ordinal would go undetected.
    // See TestQualityReviewer finding at OtelNoiseFilterTests.cs:116.

    [Fact]
    public void FilterHttpClientRequest_WhenK8sServiceHostEnvVarNotSet_NonK8sHostIsKept()
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);
        var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "https://api.github.com/repos/owner/repo");

        OtelNoiseFilter.FilterHttpClientRequest(request).Should().BeTrue();
    }

    [Fact]
    public void FilterHttpClientRequest_NullRequestUri_ReturnsTrue()
    {
        var request = new System.Net.Http.HttpRequestMessage();
        // request.RequestUri is null by default
        OtelNoiseFilter.FilterHttpClientRequest(request).Should().BeTrue(
            because: "a null URI should not crash and should default to kept");
    }

    // ── EnrichHttpClientRequest ────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "https://api.github.com/repos/owner/repo", "GET api.github.com")]
    [InlineData("POST", "http://coding-agent-api:8080/api/work-items", "POST coding-agent-api")]
    [InlineData("PUT", "https://example.com/resource/123", "PUT example.com")]
    [InlineData("DELETE", "http://service.namespace.svc.cluster.local/endpoint", "DELETE service.namespace.svc.cluster.local")]
    public void EnrichHttpClientRequest_SetsDisplayNameToMethodAndHost(
        string method, string url, string expectedDisplayName)
    {
        using var activitySource = new ActivitySource("test-source");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("original-name")!;
        var request = new System.Net.Http.HttpRequestMessage(
            new System.Net.Http.HttpMethod(method), url);

        OtelNoiseFilter.EnrichHttpClientRequest(activity, request);

        activity.DisplayName.Should().Be(expectedDisplayName);
    }

    [Fact]
    public void EnrichHttpClientRequest_NullUri_DoesNotThrowAndLeavesDisplayNameUnchanged()
    {
        using var activitySource = new ActivitySource("test-source-2");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("original-name")!;
        var request = new System.Net.Http.HttpRequestMessage();
        // request.RequestUri is null — method is non-null (HttpMethod.Get)

        var act = () => OtelNoiseFilter.EnrichHttpClientRequest(activity, request);
        act.Should().NotThrow();
        activity.DisplayName.Should().Be("original-name");
    }

    // ── ShouldDropSpan ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AgentHub/Heartbeat", true)]
    [InlineData("ComponentHub/Heartbeat", true)]
    [InlineData("AgentHub/ReportOutputLines", true)]
    [InlineData("ComponentHub/OnRenderCompleted", true)]
    [InlineData("Circuit abc123xyz", true)]
    [InlineData("Circuit ", true)]                          // "Circuit " prefix with empty id
    [InlineData("Event onclick -> SomeComponent<BuildRenderTree>b__0_12", true)]
    [InlineData("Event mouseover -> Some<Component>b__0", true)]
    // Kept
    [InlineData("Route /dashboard", false)]
    [InlineData("Route /api/work-items", false)]
    [InlineData("GET /api/work-items/", false)]
    [InlineData("AgentHub/RegisterAgent", false)]
    [InlineData("AgentHub/JobAccepted", false)]
    [InlineData("Microsoft.AspNetCore.SignalR.Server/SendToGroup", false)]
    [InlineData("ExecutePipeline", false)]
    [InlineData("Event", false)]                            // "Event" without trailing space must not match
    [InlineData("Circuit", false)]                          // "Circuit" without trailing space must not match
    public void ShouldDropSpan_ReturnsExpectedResult(string displayName, bool expectedDrop)
    {
        using var activitySource = new ActivitySource("test-should-drop");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity(displayName)!;

        OtelNoiseFilter.ShouldDropSpan(activity).Should().Be(expectedDrop,
            because: $"display name '{displayName}' should {(expectedDrop ? "" : "not ")}be dropped");
    }

    [Theory]
    [InlineData("Event onclick without arrow")]             // "Event " prefix but no " -> "
    [InlineData("Eventhandler -> something")]               // does not start with "Event " (no space)
    public void ShouldDropSpan_EventWithoutArrow_ReturnsFalse(string displayName)
    {
        using var activitySource = new ActivitySource("test-should-drop-event");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity(displayName)!;

        OtelNoiseFilter.ShouldDropSpan(activity).Should().BeFalse(
            because: $"'{displayName}' does not match the noise pattern and must be kept");
    }

    // ── OtelNoiseSpanDropProcessor wiring ─────────────────────────────────────

    [Fact]
    public void OtelNoiseSpanDropProcessor_OnStart_SuppressesNoiseSpan()
    {
        using var activitySource = new ActivitySource("test-processor");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("AgentHub/Heartbeat")!;
        var processor = new OtelNoiseSpanDropProcessor();

        // TODO: [WARNING] Add a pre-condition assertion here to make this a proper before/after proof:
        //   activity.IsAllDataRequested.Should().BeTrue("before processor runs, span should be recording");
        // Without it, if the listener/sampler config initialised IsAllDataRequested to false, the
        // processor's suppression effect would be invisible and the test would still pass.
        // See TestQualityReviewer finding at OtelNoiseFilterTests.cs:706.
        processor.OnStart(activity);

        activity.IsAllDataRequested.Should().BeFalse("noise span must not request data collection");
        activity.ActivityTraceFlags.Should().NotHaveFlag(ActivityTraceFlags.Recorded,
            because: "noise span must not be marked as recorded");
    }

    [Fact]
    public void OtelNoiseSpanDropProcessor_OnStart_DoesNotSuppressSignalSpan()
    {
        using var activitySource = new ActivitySource("test-processor-keep");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = activitySource.StartActivity("ExecutePipeline")!;
        var processor = new OtelNoiseSpanDropProcessor();

        processor.OnStart(activity);

        activity.IsAllDataRequested.Should().BeTrue("signal span must continue collecting data");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Microsoft.AspNetCore.Http.HttpContext BuildHttpContext(string path)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Path = path;
        return ctx;
    }
}
