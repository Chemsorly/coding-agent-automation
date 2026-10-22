using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Api.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// T3 coverage (spec 052 Req 14.10, design 6.0): every <c>/api/ui</c> endpoint whose route or query takes a
/// project, run, work-item, template or chat ID has a T3 (IDOR) case, marked with <see cref="IdorCaseAttribute"/>,
/// or an exemption with a reason. A new ID-taking endpoint fails here until it gets one.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class IdorCoverageTests(ApiWebApplicationFactory factory)
{
    private const string GlobalAdminOnly =
        "Settings: only the global admin role (Ui.Admin) reaches it, and it covers every project, so there is no " +
        "project boundary to cross. UiSettingsEndpointTests and the T2 matrix check the role.";

    /// <summary>Endpoints that take an ID but need no IDOR case, each with the reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["DELETE /api/ui/settings/agent-profiles/{id}"] = GlobalAdminOnly,
        ["DELETE /api/ui/settings/projects/{id}"] = GlobalAdminOnly,
        ["DELETE /api/ui/settings/projects/{projectId}/templates/{templateId}"] = GlobalAdminOnly,
        ["DELETE /api/ui/settings/provider-configs/{id}"] = GlobalAdminOnly,
        ["DELETE /api/ui/settings/quality-gate-configs/{id}"] = GlobalAdminOnly,
        ["DELETE /api/ui/settings/reviewer-configs/{id}"] = GlobalAdminOnly,
        ["GET /api/ui/settings/provider-configs/{id}/agent-labels"] = GlobalAdminOnly,
        ["POST /api/ui/settings/provider-configs/{id}/agent-labels"] = GlobalAdminOnly,
    };

    /// <summary>Every ID-taking UI endpoint, as <c>METHOD route</c>, with the ID parameters it takes.</summary>
    private IReadOnlyDictionary<string, string[]> IdTakingEndpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => ("/" + (e.RoutePattern.RawText ?? "").TrimStart('/')).StartsWith(UiEndpoints.Prefix + "/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Key: $"{method} /{(e.RoutePattern.RawText ?? "").TrimStart('/')}", Ids: IdParameters(e))))
            .Where(e => e.Ids.Length > 0)
            .ToDictionary(e => e.Key, e => e.Ids, StringComparer.Ordinal);

    private static IReadOnlyList<string> Covered =>
        typeof(IdorCoverageTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(m => m.GetCustomAttributes<IdorCaseAttribute>())
            .Select(a => a.Endpoint)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryIdTakingUiEndpoint_HasAnIdorCase_OrAnExemption()
    {
        var endpoints = IdTakingEndpoints;
        endpoints.Should().ContainKey("GET /api/ui/runs/{runId:guid}", "positive control: the run detail takes a run ID");

        var covered = Covered.ToHashSet(StringComparer.Ordinal);
        var missing = endpoints.Keys.Where(e => !covered.Contains(e) && !Exempt.ContainsKey(e))
            .Order(StringComparer.Ordinal)
            .Select(e => $"{e} (takes {string.Join(", ", endpoints[e])})");
        string.Join("\n", missing).Should().BeEmpty(
            "each ID-taking UI endpoint needs a T3 case ([IdorCase]) or an exemption with a reason");
    }

    [Fact]
    public void IdorCasesAndExemptions_NameExistingIdTakingEndpoints()
    {
        var endpoints = IdTakingEndpoints;

        Covered.Concat(Exempt.Keys).Where(e => !endpoints.ContainsKey(e))
            .Should().BeEmpty("a case or exemption for an endpoint that no longer exists, or takes no ID, is stale");
        Covered.Where(Exempt.ContainsKey).Should().BeEmpty("an endpoint with a T3 case needs no exemption");
    }

    /// <summary>
    /// The route parameters and the handler's bound parameters (query, route, <c>[AsParameters]</c> records)
    /// that carry a project, run, work-item, template or chat ID: by name, <c>id</c> or <c>…Id</c>.
    /// </summary>
    private static string[] IdParameters(RouteEndpoint endpoint)
    {
        var names = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToList();
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is { } handler)
        {
            foreach (var parameter in handler.GetParameters())
            {
                if (parameter.GetCustomAttribute<AsParametersAttribute>() is not null)
                    names.AddRange(parameter.ParameterType.GetProperties().Select(p => p.Name));
                else if (IsScalar(parameter.ParameterType))
                    names.Add(parameter.Name!);
            }
        }

        return names
            .Where(n => n.Equals("id", StringComparison.OrdinalIgnoreCase) || n.EndsWith("Id", StringComparison.Ordinal))
            .Select(n => char.ToLowerInvariant(n[0]) + n[1..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) || t == typeof(Guid) || t.IsPrimitive;
    }
}
