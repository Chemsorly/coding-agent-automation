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
///   <item><see cref="FilterAspNetCoreRequest"/> — drops health-probe and polling endpoints when the
///         request starts, so spans the request creates underneath are not recorded either.</item>
///   <item><see cref="FilterHttpClientRequest"/> — drops Kubernetes API server lease calls when they start.</item>
///   <item><see cref="ShouldDropSpan"/> — name predicate for chatty SignalR / Blazor circuit spans.</item>
///   <item><see cref="IsNoise"/> and <see cref="NameClientSpanByHost"/> — the end-of-span checks
///         <see cref="OtelNoiseSpanProcessor"/> applies to the finished span.</item>
/// </list>
/// </remarks>
public static class OtelNoiseFilter
{
    // Kubernetes in-cluster DNS alias of the API server; KUBERNETES_SERVICE_HOST holds its IP.
    private const string KubernetesApiServiceName = "kubernetes.default.svc";

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
    public static bool FilterAspNetCoreRequest(Microsoft.AspNetCore.Http.HttpContext context) =>
        !IsNoisePath(context.Request.Path.Value);

    private static bool IsNoisePath(string? path) => path is "/healthz" or "/readyz" or "/loop/status";

    // ── HttpClient request filter ─────────────────────────────────────────────

    /// <summary>
    /// Returns <c>false</c> (drop) for outbound HTTP requests to the Kubernetes API server,
    /// which produces ~387k leader-election lease spans per day.
    /// </summary>
    /// <remarks>
    /// Intended for <c>HttpClientTraceInstrumentationOptions.FilterHttpRequestMessage</c>.
    /// The request host is compared against <c>KUBERNETES_SERVICE_HOST</c> (in-cluster) and the
    /// well-known DNS alias <c>kubernetes.default.svc</c>.
    /// The env var is read per-call (not cached) to allow test isolation.
    /// </remarks>
    /// <param name="request">The outbound <see cref="System.Net.Http.HttpRequestMessage"/>.</param>
    /// <returns><c>true</c> to keep the span; <c>false</c> to drop it.</returns>
    public static bool FilterHttpClientRequest(System.Net.Http.HttpRequestMessage request) =>
        !IsKubernetesApiHost(request.RequestUri?.Host, Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));

    private static bool IsKubernetesApiHost(string? host, string? kubernetesServiceHost) =>
        host is not null
        && (string.Equals(host, kubernetesServiceHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, KubernetesApiServiceName, StringComparison.OrdinalIgnoreCase));

    // ── Span drop predicates ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the span name alone marks it as pure noise.
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

    /// <summary>
    /// Returns <c>true</c> when a finished span is noise: a noise name (<see cref="ShouldDropSpan"/>),
    /// a server span for a probe or polling path, or a client span to the Kubernetes API server.
    /// </summary>
    /// <remarks>
    /// Works on the span's final name and tags, so it catches noise whatever the instrumentation
    /// callbacks did when the span started.
    /// </remarks>
    /// <param name="activity">The finished <see cref="Activity"/>.</param>
    /// <param name="kubernetesServiceHost">The Kubernetes API server host (<c>KUBERNETES_SERVICE_HOST</c>), if any.</param>
    public static bool IsNoise(Activity activity, string? kubernetesServiceHost) =>
        ShouldDropSpan(activity)
        || (activity.Kind == ActivityKind.Server && IsNoisePath(activity.GetTagItem("url.path") as string))
        || (activity.Kind == ActivityKind.Client
            && IsKubernetesApiHost(activity.GetTagItem("server.address") as string, kubernetesServiceHost));

    // ── HttpClient span naming ────────────────────────────────────────────────

    /// <summary>
    /// Renames a finished outbound HTTP span from the bare method (<c>GET</c>) to
    /// <c>"{METHOD} {host}"</c> (e.g. <c>"GET api.github.com"</c>). No path component is included,
    /// to keep cardinality bounded. Spans from other sources are left unchanged.
    /// </summary>
    /// <param name="activity">The finished <see cref="Activity"/>.</param>
    public static void NameClientSpanByHost(Activity activity)
    {
        if (activity.Kind != ActivityKind.Client || activity.Source.Name != "System.Net.Http")
            return;

        if (activity.GetTagItem("http.request.method") is string method
            && activity.GetTagItem("server.address") is string host)
        {
            activity.DisplayName = $"{method} {host}";
        }
    }
}

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> that keeps noise spans out of the export and gives
/// outbound HTTP spans a host-qualified name.
/// </summary>
/// <remarks>
/// <para>
/// Add to the tracer provider <b>before</b> the exporter: <c>.AddProcessor(new OtelNoiseSpanProcessor())</c>.
/// Export processors skip spans that are not marked as recorded, and processors run in the order
/// they were added, so clearing the flag here keeps the span out of every exporter after it.
/// </para>
/// <para>
/// At start, spans with a noise name stop collecting data. At end, the finished span is checked again
/// against its final name and tags (<see cref="OtelNoiseFilter.IsNoise"/>), and kept client spans are
/// renamed (<see cref="OtelNoiseFilter.NameClientSpanByHost"/>). The end-of-span check does not depend
/// on the instrumentation filters having run; those still apply when the span starts, which also keeps
/// the spans a probe request creates underneath out of the trace.
/// </para>
/// </remarks>
public sealed class OtelNoiseSpanProcessor : BaseProcessor<Activity>
{
    private readonly string? _kubernetesServiceHost;

    /// <summary>
    /// Creates the processor for the current pod, reading the Kubernetes API server host from
    /// <c>KUBERNETES_SERVICE_HOST</c>.
    /// </summary>
    public OtelNoiseSpanProcessor()
        : this(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"))
    {
    }

    /// <summary>Creates the processor with an explicit Kubernetes API server host.</summary>
    /// <param name="kubernetesServiceHost">The Kubernetes API server host, or <c>null</c> outside a cluster.</param>
    public OtelNoiseSpanProcessor(string? kubernetesServiceHost)
    {
        _kubernetesServiceHost = kubernetesServiceHost;
    }

    /// <inheritdoc/>
    public override void OnStart(Activity activity)
    {
        if (OtelNoiseFilter.ShouldDropSpan(activity))
            Suppress(activity);
    }

    /// <inheritdoc/>
    public override void OnEnd(Activity activity)
    {
        if (!activity.Recorded)
            return;

        if (OtelNoiseFilter.IsNoise(activity, _kubernetesServiceHost))
        {
            Suppress(activity);
            return;
        }

        OtelNoiseFilter.NameClientSpanByHost(activity);
    }

    private static void Suppress(Activity activity)
    {
        activity.IsAllDataRequested = false;
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
}
