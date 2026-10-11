using CodingAgent.Api.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>Which credentials an endpoint declares it accepts (spec 051 D1).</summary>
internal enum EndpointClass
{
    /// <summary>Explicitly anonymous (health probes only).</summary>
    Anonymous,

    /// <summary>API keys: <c>AgentApiKey</c> and/or <c>OperatorApiKey</c>.</summary>
    Service,

    /// <summary>User token: one or more <see cref="UiPolicies"/>.</summary>
    Ui,

    /// <summary>No policy at all: only the deny fallback applies.</summary>
    Undeclared,

    /// <summary>Policies of both surfaces on one endpoint.</summary>
    Mixed,
}

/// <summary>One mapped endpoint with the authorization it declares.</summary>
internal sealed record EndpointAuth(string Method, RoutePattern Pattern, bool AllowAnonymous, IReadOnlyList<string> Policies, bool IsHub)
{
    private static readonly HashSet<string> ServicePolicies = new(StringComparer.Ordinal) { ApiAuthPolicies.Agent, ApiAuthPolicies.Operator };

    public string Route => "/" + (Pattern.RawText ?? "").TrimStart('/');

    public EndpointClass Class
    {
        get
        {
            if (AllowAnonymous)
                return EndpointClass.Anonymous;
            if (Policies.Count == 0)
                return EndpointClass.Undeclared;
            if (Policies.All(ServicePolicies.Contains))
                return EndpointClass.Service;
            if (Policies.All(UiPolicies.All.ContainsKey))
                return EndpointClass.Ui;
            return EndpointClass.Mixed;
        }
    }

    /// <summary>One snapshot line, e.g. <c>GET /api/ui/me -> Ui: Ui.AnyAccess</c>.</summary>
    public string Describe() =>
        $"{Method} {Route}{(IsHub ? " (hub)" : "")} -> {Class}" + (Policies.Count > 0 ? ": " + string.Join(", ", Policies) : "");

    /// <summary>A request path for this route with every parameter filled in.</summary>
    public string SamplePath()
    {
        var parts = Pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => SampleValue(parameter),
            _ => "x",
        })));
        return "/" + string.Join("/", parts);
    }

    private static string SampleValue(RoutePatternParameterPart parameter)
    {
        var constraints = parameter.ParameterPolicies.Select(p => p.Content).ToList();
        if (constraints.Contains("guid"))
            return Guid.NewGuid().ToString();
        if (constraints.Contains("int") || constraints.Contains("long"))
            return "1";
        return "x";
    }
}

internal static class EndpointInventory
{
    /// <summary>Every route endpoint of the host, one entry per HTTP method, in a stable order.</summary>
    public static IReadOnlyList<EndpointAuth> Read(IServiceProvider services) =>
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
                var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                var isHub = endpoint.Metadata.GetMetadata<HubMetadata>() is not null;
                return methods.Select(method => new EndpointAuth(method, endpoint.RoutePattern, allowAnonymous, policies, isHub));
            })
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();
}
