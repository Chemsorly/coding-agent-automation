using System.Diagnostics;
using OpenTelemetry;

namespace CodingAgent.Infrastructure.Telemetry;

/// <summary>
/// Shared noise-reduction helpers for OpenTelemetry instrumentation.
/// Used in all four long-lived hosts (Api, Web, Scheduler, JobController) to drop
/// high-cardinality, low-signal spans that account for ~70% of daily trace volume.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="FilterAspNetCoreRequest"/> — drops health-probe and polling endpoints.</item>
///   <item><see cref="FilterHttpClientRequest"/> — drops Kubernetes API server lease calls.</item>
///   <item><see cref="EnrichHttpClientRequest"/> — normalises outbound span names to <c>METHOD host</c>.</item>
///   <item><see cref="ShouldDropSpan"/> — predicate used by <see cref="OtelNoiseSpanDropProcessor"/>
///         to suppress chatty SignalR / Blazor circuit spans.</item>
/// </list>
/// </remarks>
public static class OtelNoiseFilter
{
    // ── AspNetCore request filter ─────────────────────────────────────────────

    /// <summary>
    /// Returns <c>false</c> (drop) for health-probe and polling request paths that produce
    /// no diagnostic value and account for hundreds of thousands of spans per day.
    /// </summary>
    /// <remarks>
    /// Intended for <c>AspNetCoreTraceInstrumentationOptions.Filter</c>.
    /// Uses exact path matching — sub-paths such as <c>/healthz/detail</c> are kept.
    /// </remarks>
    /// <param name="context">The <see cref="Microsoft.AspNetCore.Http.HttpContext"/> for the incoming request.</param>
    /// <returns><c>true</c> to keep the span; <c>false</c> to drop it.</returns>
    public static bool FilterAspNetCoreRequest(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null) return true;

        return path is not ("/healthz" or "/readyz" or "/loop/status");
    }

    // ── HttpClient request filter ─────────────────────────────────────────────

    /// <summary>
    /// Returns <c>false</c> (drop) for outbound HTTP requests to the Kubernetes API server,
    /// which produces ~387k leader-election lease spans per day.
    /// </summary>
    /// <remarks>
    /// Intended for <c>HttpClientTraceInstrumentationOptions.FilterHttpRequestMessage</c>.
    /// Detection order:
    /// <list type="number">
    ///   <item>If <c>KUBERNETES_SERVICE_HOST</c> is set (in-cluster), compare the request host against it.</item>
    ///   <item>Fall back to matching against the well-known DNS alias <c>kubernetes.default.svc</c>.</item>
    /// </list>
    /// The env var is read per-call (not cached) to allow test isolation.
    /// </remarks>
    /// <param name="request">The outbound <see cref="System.Net.Http.HttpRequestMessage"/>.</param>
    /// <returns><c>true</c> to keep the span; <c>false</c> to drop it.</returns>
    public static bool FilterHttpClientRequest(System.Net.Http.HttpRequestMessage request)
    {
        var host = request.RequestUri?.Host;
        if (host is null) return true;

        // TODO: [WARNING] KUBERNETES_SERVICE_HOST is read on every outbound HttpClient span (hot path).
        // GetEnvironmentVariable acquires a native lock per call. The var is set once at pod startup and
        // never changes at runtime — cache it as a static readonly field to eliminate the per-call overhead.
        // The current per-call read was chosen for test isolation, but tests already restore the var via
        // IDisposable, so caching is safe. See DotNetSpecialist review finding at OtelNoiseFilter.cs:62.
        var k8sHost = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (k8sHost is not null && string.Equals(host, k8sHost, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(host, "kubernetes.default.svc", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    // ── HttpClient span name enrichment ───────────────────────────────────────

    /// <summary>
    /// Renames outbound HTTP spans from the uninformative default (<c>GET</c>, <c>POST</c>) to
    /// <c>"{METHOD} {host}"</c> (e.g. <c>"GET api.github.com"</c>).
    /// </summary>
    /// <remarks>
    /// Intended for <c>HttpClientTraceInstrumentationOptions.EnrichWithHttpRequestMessage</c>.
    /// No path component is included to keep cardinality bounded.
    /// </remarks>
    /// <param name="activity">The <see cref="Activity"/> to rename.</param>
    /// <param name="request">The outbound <see cref="System.Net.Http.HttpRequestMessage"/>.</param>
    public static void EnrichHttpClientRequest(Activity activity, System.Net.Http.HttpRequestMessage request)
    {
        var method = request.Method?.Method;
        var host = request.RequestUri?.Host;

        if (method is not null && host is not null)
            activity.DisplayName = $"{method} {host}";
    }

    // ── Span drop predicate ───────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the span is pure noise and should be suppressed by
    /// <see cref="OtelNoiseSpanDropProcessor"/>.
    /// </summary>
    /// <remarks>
    /// Dropped patterns:
    /// <list type="bullet">
    ///   <item>SignalR hub methods: <c>*/Heartbeat</c>, <c>*/ReportOutputLines</c>, <c>*/OnRenderCompleted</c></item>
    ///   <item>Blazor circuit lifecycle: spans starting with <c>"Circuit "</c></item>
    ///   <item>Blazor event callbacks: spans starting with <c>"Event "</c> and containing <c>" -> "</c></item>
    /// </list>
    /// Explicitly kept: Blazor route spans starting with <c>"Route "</c>.
    /// </remarks>
    /// <param name="activity">The <see cref="Activity"/> being evaluated.</param>
    /// <returns><c>true</c> to drop the span; <c>false</c> to keep it.</returns>
    public static bool ShouldDropSpan(Activity activity)
    {
        var name = activity.DisplayName;
        if (name is null) return false;

        // SignalR hub method noise
        if (name.EndsWith("/Heartbeat", StringComparison.Ordinal)) return true;
        if (name.EndsWith("/ReportOutputLines", StringComparison.Ordinal)) return true;
        if (name.EndsWith("/OnRenderCompleted", StringComparison.Ordinal)) return true;

        // Blazor circuit lifecycle (high-cardinality IDs in the name)
        if (name.StartsWith("Circuit ", StringComparison.Ordinal)) return true;

        // Blazor event callbacks (e.g. "Event onclick -> Component<BuildRenderTree>b__0_12")
        if (name.StartsWith("Event ", StringComparison.Ordinal) && name.Contains(" -> ", StringComparison.Ordinal))
            return true;

        return false;
    }
}

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> that suppresses spans identified as noise
/// by <see cref="OtelNoiseFilter.ShouldDropSpan"/>.
/// </summary>
/// <remarks>
/// Add to the tracer provider via <c>.AddProcessor(new OtelNoiseSpanDropProcessor())</c>.
/// The processor runs at span start; suppressed spans are marked as not recording so no
/// data is collected and nothing is exported.
/// </remarks>
public sealed class OtelNoiseSpanDropProcessor : BaseProcessor<Activity>
{
    /// <inheritdoc/>
    public override void OnStart(Activity activity)
    {
        if (OtelNoiseFilter.ShouldDropSpan(activity))
        {
            activity.IsAllDataRequested = false;
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
