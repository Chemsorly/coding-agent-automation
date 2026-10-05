using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Telemetry;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="OtelNoiseFilter"/> filter predicates and the
/// <see cref="OtelNoiseSpanProcessor"/> (start and end-of-span checks, client span naming).
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

    // ── OtelNoiseSpanProcessor ────────────────────────────────────────────────

    [Fact]
    public void Processor_OnStart_SuppressesNoiseSpan()
    {
        using var activity = StartRecordedActivity("test-processor", "AgentHub/Heartbeat", ActivityKind.Server);
        activity.Recorded.Should().BeTrue("the span is recorded before the processor runs");

        new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnStart(activity);

        activity.IsAllDataRequested.Should().BeFalse("noise span must not request data collection");
        activity.Recorded.Should().BeFalse("noise span must not be exported");
    }

    [Fact]
    public void Processor_OnStart_DoesNotSuppressSignalSpan()
    {
        using var activity = StartRecordedActivity("test-processor", "ExecutePipeline", ActivityKind.Internal);

        new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnStart(activity);

        activity.IsAllDataRequested.Should().BeTrue("signal span must continue collecting data");
        activity.Recorded.Should().BeTrue();
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/loop/status")]
    public void Processor_OnEnd_SuppressesProbeServerSpans(string path)
    {
        // The instrumentation filter did not drop the span when it started: the end-of-span check must.
        using var activity = StartRecordedActivity("Microsoft.AspNetCore", $"GET {path}", ActivityKind.Server);
        activity.SetTag("url.path", path);

        new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnEnd(activity);

        activity.Recorded.Should().BeFalse($"a finished {path} server span must not be exported");
    }

    [Theory]
    [InlineData("/api/work-items/pending")]
    [InlineData("/healthz/detail")]
    public void Processor_OnEnd_KeepsOtherServerSpans(string path)
    {
        using var activity = StartRecordedActivity("Microsoft.AspNetCore", $"GET {path}", ActivityKind.Server);
        activity.SetTag("url.path", path);

        new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnEnd(activity);

        activity.Recorded.Should().BeTrue();
        activity.DisplayName.Should().Be($"GET {path}", "server span names are left unchanged");
    }

    [Theory]
    [InlineData("10.43.0.1")]
    [InlineData("kubernetes.default.svc")]
    public void Processor_OnEnd_SuppressesKubernetesApiClientSpans(string host)
    {
        using var activity = StartHttpClientActivity("PUT", host);

        new OtelNoiseSpanProcessor(kubernetesServiceHost: "10.43.0.1").OnEnd(activity);

        activity.Recorded.Should().BeFalse($"a call to the Kubernetes API server ({host}) must not be exported");
    }

    [Theory]
    [InlineData("GET", "api.github.com", "GET api.github.com")]
    [InlineData("POST", "coding-agent-api", "POST coding-agent-api")]
    [InlineData("DELETE", "service.namespace.svc.cluster.local", "DELETE service.namespace.svc.cluster.local")]
    public void Processor_OnEnd_NamesHttpClientSpansByMethodAndHost(string method, string host, string expectedName)
    {
        using var activity = StartHttpClientActivity(method, host);

        new OtelNoiseSpanProcessor(kubernetesServiceHost: "10.43.0.1").OnEnd(activity);

        activity.Recorded.Should().BeTrue();
        activity.DisplayName.Should().Be(expectedName);
    }

    [Fact]
    public void Processor_OnEnd_LeavesClientSpansOfOtherSourcesUnchanged()
    {
        using var activity = StartRecordedActivity("Npgsql", "coding_agent", ActivityKind.Client);
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("server.address", "postgres");

        new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnEnd(activity);

        activity.DisplayName.Should().Be("coding_agent");
    }

    [Fact]
    public void Processor_OnEnd_LeavesHttpClientSpanWithoutHostUnchanged()
    {
        using var activity = StartRecordedActivity("System.Net.Http", "GET", ActivityKind.Client);
        activity.SetTag("http.request.method", "GET");

        var act = () => new OtelNoiseSpanProcessor(kubernetesServiceHost: null).OnEnd(activity);

        act.Should().NotThrow();
        activity.DisplayName.Should().Be("GET");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Activity StartHttpClientActivity(string method, string host)
    {
        var activity = StartRecordedActivity("System.Net.Http", method, ActivityKind.Client);
        activity.SetTag("http.request.method", method);
        activity.SetTag("server.address", host);
        return activity;
    }

    private static Activity StartRecordedActivity(string sourceName, string name, ActivityKind kind)
    {
        // The source and listener only need to live until the activity has started.
        using var source = new ActivitySource(sourceName);
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return source.StartActivity(name, kind)!;
    }

    private static Microsoft.AspNetCore.Http.HttpContext BuildHttpContext(string path)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Path = path;
        return ctx;
    }
}
