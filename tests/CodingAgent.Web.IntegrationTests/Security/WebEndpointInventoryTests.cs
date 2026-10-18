using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Security;

/// <summary>
/// T7 (spec 052 Req 14.1): every endpoint of the web host, classified by what it requires, pinned in
/// <c>web-endpoint-authorization.snapshot.txt</c>. A new or changed endpoint changes the snapshot, so its
/// protection shows up in the PR diff. The host runs in Production, as deployed.
/// </summary>
[Collection("SmokeTests")]
public sealed partial class WebEndpointInventoryTests : IClassFixture<ProductionWebApplicationFactory>
{
    private const string SnapshotFile = "web-endpoint-authorization.snapshot.txt";

    /// <summary>Req 14.1: the anonymous allow-list, besides the static assets.</summary>
    private static readonly HashSet<string> AnonymousAllowed = new(StringComparer.Ordinal)
    {
        "GET /", "GET /login", "POST /login", "HEAD /login",
        "POST /auth/login", "GET /auth/oidc", "POST /auth/logout",
        "GET /healthz", "HEAD /healthz", "GET /readyz", "HEAD /readyz",
    };

    /// <summary>The two forwarders, the only endpoints under /api and /hubs.</summary>
    private static readonly string[] ForwarderRoutes = ["/api/ui/{**rest}", "/hubs/ui"];

    // The fingerprint Blazor puts into the resource-collection file name, e.g. resource-collection.yI4pu.js or
    // resource-collection._MpCU.js (base64url, so it may hold '_' and '-').
    [GeneratedRegex(@"^/_framework/resource-collection(\.[A-Za-z0-9_-]+)?\.js(\.gz|\.br)?$")]
    private static partial Regex ResourceCollectionRoute();

    private readonly ProductionWebApplicationFactory _factory;

    public WebEndpointInventoryTests(ProductionWebApplicationFactory factory) => _factory = factory;

    private IReadOnlyList<WebEndpoint> Endpoints
    {
        get
        {
            _factory.CreateClient().Dispose();
            return WebEndpoint.Read(_factory.Services);
        }
    }

    [Fact]
    public void Inventory_MatchesTheSnapshot()
    {
        var lines = Endpoints.Select(e => e.Describe()).Distinct(StringComparer.Ordinal).ToList();
        var actual = string.Join("\n", lines) + "\n";
        var snapshotPath = Path.Combine(SourceDirectory(), SnapshotFile);
        var expected = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath).ReplaceLineEndings("\n") : "";

        if (actual != expected)
        {
            File.WriteAllText(Path.ChangeExtension(snapshotPath, ".received.txt"), actual);
            actual.Should().Be(expected,
                $"the web host's endpoints changed. Review {Path.GetFileName(Path.ChangeExtension(snapshotPath, ".received.txt"))} " +
                $"next to the snapshot and, if every line is intended, copy it over {SnapshotFile}");
        }
    }

    [Fact]
    public void EveryEndpoint_IsClassified()
    {
        Endpoints.Where(e => e.Class == WebEndpointClass.Unclassified)
            .Select(e => e.Describe())
            .Should().BeEmpty("an endpoint must be anonymous on the allow-list, a static asset, a forwarder, a page or behind the cookie fallback");
    }

    [Fact]
    public void OnlyTheAllowList_AndStaticAssets_AreAnonymous()
    {
        Endpoints.Where(e => e.Class == WebEndpointClass.Anonymous && !AnonymousAllowed.Contains($"{e.Method} {e.Route}"))
            .Select(e => e.Describe())
            .Should().BeEmpty("Req 14.1 lists every anonymous endpoint");
        Endpoints.Where(e => e.Class == WebEndpointClass.Anonymous).Select(e => $"{e.Method} {e.Route}")
            .Should().BeEquivalentTo(AnonymousAllowed, "each allow-listed endpoint is mapped");
    }

    [Fact]
    public void StaticAssets_AreFilesOutsideTheApiAuthAndHubPaths()
    {
        var assets = Endpoints.Where(e => e.Class == WebEndpointClass.StaticAsset).ToList();

        assets.Should().NotBeEmpty("positive control: the WebAssembly runtime and wwwroot are mapped");
        assets.Should().OnlyContain(e => e.Method == "GET" || e.Method == "HEAD");
        assets.Select(e => e.Route).Should().NotContain(route =>
            route.StartsWith("/api/", StringComparison.Ordinal) || route.StartsWith("/bff/", StringComparison.Ordinal)
            || route.StartsWith("/hubs/", StringComparison.Ordinal) || route.StartsWith("/auth/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheForwarders_AreTheOnlyApiAndHubEndpoints_AndRequireAccess()
    {
        var underApiOrHubs = Endpoints
            .Where(e => e.Route.StartsWith("/api", StringComparison.Ordinal) || e.Route.StartsWith("/hubs", StringComparison.Ordinal))
            .ToList();

        underApiOrHubs.Select(e => e.Route).Distinct().Should().BeEquivalentTo(ForwarderRoutes);
        underApiOrHubs.Should().OnlyContain(e => e.Class == WebEndpointClass.Forwarder);
    }

    [Fact]
    public void EveryClientPage_IsMapped_WithItsPagePolicy()
    {
        var pages = Endpoints.Where(e => e.Class == WebEndpointClass.Page).ToList();
        var routable = typeof(CodingAgent.Web.Components.Routes).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (Type: t, r.Template)))
            .ToList();

        routable.Should().NotBeEmpty();
        foreach (var (type, template) in routable)
        {
            var route = "/" + template.TrimStart('/');
            var mapped = pages.Where(e => string.Equals(e.Route, route, StringComparison.OrdinalIgnoreCase)).ToList();
            mapped.Should().NotBeEmpty($"{type.Name} ({route}) must be served by the web host");
            // The folder-wide [Authorize] (a signed-in user) plus the page's own policy, if it has one.
            string[] declared = ["(default)", .. type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy).OfType<string>()];
            foreach (var endpoint in mapped)
                endpoint.Policies.Should().Contain(declared, $"{type.Name} ({route}) must keep its page policy");
        }
    }

    [Fact]
    public void TheSessionEndpoint_IsBehindTheCookieFallback()
    {
        Endpoints.Where(e => e.Route == "/bff/session").Select(e => e.Describe())
            .Should().Equal("GET /bff/session -> Cookie (fallback)");
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private enum WebEndpointClass
    {
        /// <summary>Explicitly anonymous; Req 14.1 lists them.</summary>
        Anonymous,

        /// <summary>A build or wwwroot file (MapStaticAssets), anonymous.</summary>
        StaticAsset,

        /// <summary>A BFF forwarder (<see cref="AccessPolicies.AnyAccess"/>).</summary>
        Forwarder,

        /// <summary>A routable page: the cookie fallback plus its page policy.</summary>
        Page,

        /// <summary>No policy of its own: the fallback policy (an authenticated cookie user) applies.</summary>
        CookieFallback,

        Unclassified,
    }

    private sealed record WebEndpoint(string Method, string Route, bool AllowAnonymous, bool IsStaticAsset, bool IsPage, IReadOnlyList<string> Policies)
    {
        public WebEndpointClass Class
        {
            get
            {
                if (IsStaticAsset)
                    return AllowAnonymous ? WebEndpointClass.StaticAsset : WebEndpointClass.Unclassified;
                if (AllowAnonymous)
                    return WebEndpointClass.Anonymous;
                if (ForwarderRoutes.Contains(Route))
                    return Policies.SequenceEqual([AccessPolicies.AnyAccess]) ? WebEndpointClass.Forwarder : WebEndpointClass.Unclassified;
                if (IsPage)
                    return Policies.Contains("(default)") ? WebEndpointClass.Page : WebEndpointClass.Unclassified;
                return Policies.Count == 0 ? WebEndpointClass.CookieFallback : WebEndpointClass.Unclassified;
            }
        }

        /// <summary>
        /// One snapshot line. Static assets collapse into one line per method (their names carry build
        /// fingerprints), and so do the fingerprinted resource-collection files.
        /// </summary>
        public string Describe()
        {
            var route = IsStaticAsset ? "<static assets>" : ResourceCollectionRoute().IsMatch(Route) ? "/_framework/resource-collection{.fingerprint}.js" : Route;
            var label = Class switch
            {
                WebEndpointClass.CookieFallback => "Cookie (fallback)",
                WebEndpointClass.StaticAsset => "Anonymous",
                _ => Class.ToString(),
            };
            return $"{Method} {route} -> {label}" + (Policies.Count > 0 ? ": " + string.Join(", ", Policies) : "");
        }

        public static List<WebEndpoint> Read(IServiceProvider services) =>
            services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint =>
                {
                    var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } declared
                        ? declared.Order(StringComparer.Ordinal).ToList()
                        : ["*"];
                    var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                        .Select(a => a.Policy ?? "(default)")
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToList();
                    var route = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
                    var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                    var isStaticAsset = endpoint.Metadata.GetMetadata<StaticAssetDescriptor>() is not null;
                    var isPage = endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;
                    return methods.Select(method => new WebEndpoint(method, route, allowAnonymous, isStaticAsset, isPage, policies));
                })
                .OrderBy(e => e.IsStaticAsset)
                .ThenBy(e => e.Route, StringComparer.Ordinal)
                .ThenBy(e => e.Method, StringComparer.Ordinal)
                .ToList();
    }
}
