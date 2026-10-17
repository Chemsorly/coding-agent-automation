using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// T9 (spec 052 Req 14.5): every <c>/api/ui</c> endpoint answers below 500 to hostile route and query values sent
/// with a valid user token. It is driven by the endpoint inventory like T2: each route value and each scalar query or
/// <c>[AsParameters]</c> field gets each hostile value in turn, the others a valid sample. A global admin token passes
/// authorization, so the handler runs. JSON bodies are out of scope; a body-taking endpoint gets <c>{}</c>.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiInputRobustnessTests(ApiWebApplicationFactory factory)
{
    /// <summary>Negative and very large numbers, a 5 KB string, an invalid GUID and date, an unknown enum name.</summary>
    private static readonly string[] HostileValues =
        ["-1", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), new string('x', 5 * 1024), "not-a-guid", "9999-99-99", "NotAKnownValue"];

    [Fact]
    public async Task EveryUiEndpoint_AnswersBelow500_ToHostileRouteAndQueryValues()
    {
        using var client = AdminClient();

        var failures = new List<string>();
        var cases = 0;
        foreach (var endpoint in UiApiEndpoints())
        {
            foreach (var parameter in endpoint.Parameters)
            {
                foreach (var value in HostileValues)
                {
                    cases++;
                    using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Url(parameter, value));
                    if (endpoint.Method is "POST" or "PUT" or "PATCH")
                        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    if ((int)response.StatusCode >= 500)
                        failures.Add($"{endpoint.Method} {endpoint.Route} {parameter.Name}={Describe(value)} -> {(int)response.StatusCode}");
                }
            }
        }

        cases.Should().BeGreaterThan(100, "positive control: the inventory yields the UI endpoints' parameters");
        string.Join("\n", failures).Should().BeEmpty("a hostile value must be refused with 400, never answered with 5xx");
    }

    /// <summary>
    /// The fixes T9 asked for, one line each (Task 6.2): a paged read out of bounds answers 400 with the reason,
    /// never 500 and never a clamped page. The first three are what T9 found on <c>/api/ui/runs</c>.
    /// </summary>
    [Theory]
    [InlineData("/api/ui/runs?page=-1")]
    [InlineData("/api/ui/runs?pageSize=-1")]
    [InlineData("/api/ui/runs?pageSize=2147483647")]
    [InlineData("/api/ui/runs?pageSize=5000")]
    [InlineData("/api/ui/runs?page=0")]
    [InlineData("/api/ui/runs?page=2147483647&pageSize=50")]
    [InlineData("/api/ui/triages?page=0")]
    [InlineData("/api/ui/triages?pageSize=201")]
    [InlineData("/api/ui/templates/any/issues?page=0")]
    [InlineData("/api/ui/templates/any/issues/readiness?page=0")]
    [InlineData("/api/ui/templates/any/pull-requests?page=0")]
    [InlineData("/api/ui/templates/any/epics?page=0")]
    public async Task OutOfBoundsPaging_Answers400_WithTheReason(string url)
    {
        using var client = AdminClient();
        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("page");
    }

    /// <summary>D8: the largest page each store serves still answers 200; About.razor asks runs for 1000.</summary>
    [Theory]
    [InlineData("/api/ui/runs?pageSize=1000")]
    [InlineData("/api/ui/triages?pageSize=200")]
    public async Task TheLargestAllowedPage_Answers200(string url)
    {
        using var client = AdminClient();
        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private HttpClient AdminClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestUserTokens.For(TestUserTokens.Global(AccessRole.Admin)));
        return client;
    }

    private static string Describe(string value) => value.Length > 40 ? $"<{value.Length} chars>" : value;

    private IEnumerable<UiEndpoint> UiApiEndpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => ("/" + (e.RoutePattern.RawText ?? "").TrimStart('/')).StartsWith(UiEndpoints.Prefix + "/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => new UiEndpoint(method, e.RoutePattern, QueryParameters(e))))
            .Where(e => e.Parameters.Count > 0)
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal);

    /// <summary>The handler's scalar parameters bound from the query: plain ones and the fields of [AsParameters] records.</summary>
    private static IReadOnlyList<(string Name, Type Type)> QueryParameters(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler)
            return [];
        var routeNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, Type)>();
        foreach (var parameter in handler.GetParameters())
        {
            if (parameter.GetCustomAttribute<AsParametersAttribute>() is not null)
            {
                result.AddRange(parameter.ParameterType.GetProperties()
                    .Where(p => IsScalar(p.PropertyType) && !routeNames.Contains(p.Name))
                    .Select(p => (p.Name, p.PropertyType)));
            }
            else if (IsScalar(parameter.ParameterType) && !routeNames.Contains(parameter.Name!)
                     && parameter.GetCustomAttribute<FromBodyAttribute>() is null
                     && parameter.GetCustomAttribute<FromServicesAttribute>() is null
                     && parameter.GetCustomAttribute<FromHeaderAttribute>() is null)
            {
                var name = parameter.GetCustomAttribute<FromQueryAttribute>()?.Name ?? parameter.Name!;
                result.Add((name, parameter.ParameterType));
            }
        }
        return result;
    }

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) || t == typeof(Guid) || t == typeof(DateTimeOffset) || t == typeof(DateTime)
               || t.IsPrimitive || t.IsEnum || t == typeof(decimal);
    }

    /// <summary>A UI endpoint and the values it takes: route parameters first, then query parameters.</summary>
    private sealed record UiEndpoint(string Method, RoutePattern Pattern, IReadOnlyList<(string Name, Type Type)> Query)
    {
        public string Route => "/" + (Pattern.RawText ?? "").TrimStart('/');

        public IReadOnlyList<(string Name, bool InRoute)> Parameters =>
            [.. Pattern.Parameters.Select(p => (p.Name, true)), .. Query.Select(q => (q.Name, false))];

        /// <summary>The request URL with <paramref name="value"/> in <paramref name="parameter"/> and valid samples elsewhere.</summary>
        public string Url((string Name, bool InRoute) parameter, string value)
        {
            var path = string.Join("/", Pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart routeParameter when parameter.InRoute && routeParameter.Name == parameter.Name =>
                    Uri.EscapeDataString(value),
                RoutePatternParameterPart routeParameter => SampleRouteValue(routeParameter),
                _ => "x",
            }))));
            var query = Query.Select(q => $"{Uri.EscapeDataString(q.Name)}=" + Uri.EscapeDataString(
                !parameter.InRoute && q.Name == parameter.Name ? value : SampleQueryValue(q.Type)));
            return "/" + path + "?" + string.Join("&", query);
        }

        private static string SampleRouteValue(RoutePatternParameterPart parameter)
        {
            var constraints = parameter.ParameterPolicies.Select(p => p.Content).ToList();
            if (constraints.Contains("guid"))
                return Guid.NewGuid().ToString();
            return constraints.Contains("int") || constraints.Contains("long") ? "1" : "x";
        }

        private static string SampleQueryValue(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(Guid))
                return Guid.NewGuid().ToString();
            if (t.IsEnum)
                return Enum.GetNames(t)[0];
            if (t == typeof(bool))
                return "false";
            if (t == typeof(DateTimeOffset) || t == typeof(DateTime))
                return DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return t == typeof(string) ? "x" : "1";
        }
    }
}
