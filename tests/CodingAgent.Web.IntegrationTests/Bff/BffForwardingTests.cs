using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.Bff;
using CodingAgent.Web.IntegrationTests.Helpers;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>
/// Spec 052 Req 2, 9.1: the BFF forwards exactly <c>/api/ui/**</c> and <c>/hubs/ui</c> to the API
/// with a minted user token, strips the browser's credentials, and refuses everything else before
/// it reaches the API, in the order of Req 2.11.
/// </summary>
[Collection("SmokeTests")]
public class BffForwardingTests : IClassFixture<BffWebApplicationFactory>
{
    private readonly BffWebApplicationFactory _factory;

    public BffForwardingTests(BffWebApplicationFactory factory) => _factory = factory;

    private StubPipelineApi Api => _factory.Api;

    private static HttpRequestMessage Get(string path, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (csrf)
            request.Headers.Add(BffRequestGuard.CsrfHeader, "1");
        return request;
    }

    /// <summary>The requests the stub API received while <paramref name="act"/> ran.</summary>
    private async Task<(T Result, IReadOnlyList<RecordedRequest> Forwarded)> ObserveAsync<T>(Func<Task<T>> act)
    {
        var before = Api.Requests.Count;
        var result = await act();
        return (result, Api.Requests.Skip(before).ToList());
    }

    private static async Task<ClaimsIdentity> ValidateAsync(string authorization)
    {
        authorization.Should().StartWith("Bearer ");
        var key = UserTokens.CreateSigningKey(AuthTestEnvironment.UserTokenSigningKey);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(authorization["Bearer ".Length..], UserTokens.CreateValidationParameters(key));
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        return result.ClaimsIdentity;
    }

    [Fact]
    public void DefaultActivityTimeout_IsAtLeastTheChatTimeout() =>
        BffForwarding.ActivityTimeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(5), "D11: chat launch may take minutes");

    [Fact]
    public async Task ApiUiRequest_IsForwarded_WithPathQueryAndBody_AndOnlyTheUsersToken()
    {
        using var client = await _factory.SignInClientAsync("reader");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ui/runs/search?page=2&q=a%20b")
        {
            Content = new StringContent("{\"x\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(BffRequestGuard.CsrfHeader, "1");
        request.Headers.Add("Origin", _factory.Origin);
        request.Headers.Add("X-Api-Key", "browser-supplied-key");
        request.Headers.Authorization = new("Bearer", "browser-supplied-token");

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(request));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var upstream = forwarded.Should().ContainSingle().Subject;
        upstream.Method.Should().Be("POST");
        upstream.PathAndQuery.Should().Be("/api/ui/runs/search?page=2&q=a%20b");
        upstream.Body.Should().Be("{\"x\":1}");
        upstream.Headers.Should().NotContainKeys("Cookie", "X-Api-Key", BffRequestGuard.CsrfHeader);

        var identity = await ValidateAsync(upstream.Headers["Authorization"]);
        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-reader");
        identity.FindFirst(UserTokenClaimTypes.Use).Should().BeNull("HTTP tokens are not hub tokens");
        AccessGrantClaims.FromClaims(identity.Claims).GlobalRole.Should().Be(AccessRole.ReadOnly);
    }

    [Fact]
    public async Task Response_LosesTheApisSetCookie_AndIsNotCached()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var response = await client.Http.SendAsync(Get("/api/ui/status/200"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle("the forwarded response carries the web host's headers once");
        response.Headers.GetValues(SecurityHeadersRegistration.AppVersionHeader).Should().ContainSingle();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(502)]
    public async Task ApiStatusAndBody_PassThrough(int status)
    {
        using var client = await _factory.SignInClientAsync("reader");

        var response = await client.Http.SendAsync(Get($"/api/ui/status/{status}"));

        ((int)response.StatusCode).Should().Be(status);
        (await response.Content.ReadAsStringAsync()).Should().Be($"\"reason-{status}\"");
    }

    [Fact]
    public async Task NoSession_Is401_AndNothingIsForwarded()
    {
        using var client = _factory.Anonymous();

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(Get("/api/ui/runs")));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull("API-style paths get a status code, not a login redirect");
        forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task NoAccessUser_Is403_AndNothingIsForwarded()
    {
        using var client = await _factory.SignInClientAsync("carol", "unbound-group");

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(Get("/api/ui/runs")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a 401 would send a no-access user to the login page in a loop");
        forwarded.Should().BeEmpty();
    }

    public static TheoryData<string, string?, string?> CsrfViolations => new()
    {
        { "missing header", null, null },
        { "foreign Origin", "https://evil.example", null },
        { "cross-site fetch", null, "cross-site" },
    };

    [Theory]
    [MemberData(nameof(CsrfViolations))]
    public async Task CsrfViolation_Is400_AndNothingIsForwarded(string violation, string? origin, string? fetchSite)
    {
        using var client = await _factory.SignInClientAsync("reader");
        var request = Get("/api/ui/settings/models", csrf: origin is not null || fetchSite is not null);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (fetchSite is not null)
            request.Headers.Add("Sec-Fetch-Site", fetchSite);

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(request));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, violation);
        forwarded.Should().BeEmpty(violation);
    }

    /// <summary>Req 2.11: the guard answers before authentication, then the session, then routing.</summary>
    [Fact]
    public async Task Refusals_FollowTheOrderOfReq2_11()
    {
        using var anonymous = _factory.Anonymous();
        using var reader = await _factory.SignInClientAsync("reader");

        (await anonymous.Http.SendAsync(Get("/api/ui/me", csrf: false))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.Http.SendAsync(Get("/api/ui/me"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.Http.SendAsync(Get("/api/config/export"))).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "only /api/ui/** is forwarded");
        (await anonymous.Http.SendAsync(Get("/bff/unknown"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.Http.SendAsync(Get("/bff/unknown"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OtherApiPaths_AreNeverForwarded()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var (responses, forwarded) = await ObserveAsync(async () => new[]
        {
            await client.Http.SendAsync(Get("/api/config/export")),
            await client.Http.SendAsync(Get("/api/work-items/pending")),
            await client.Http.SendAsync(Get("/api/uix")),
        });

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.NotFound);
        forwarded.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/ui/..%2Fconfig/export")]
    [InlineData("/api/ui/..%2fconfig/export")]
    [InlineData("/api/ui/%5C..%5Cconfig")]
    public async Task EncodedSlash_Is400_AndNothingIsForwarded(string path)
    {
        using var client = await _factory.SignInClientAsync("reader");

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(Get(path)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task DotSegments_CannotLeaveTheProxiedPrefix()
    {
        using var client = await _factory.SignInClientAsync("reader");

        // The client and Kestrel both resolve the dot segments, so this arrives as /api/agents.
        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(Get("/api/ui/%2e%2e/agents")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task ApiTimeout_Is504_WithTheShortTestTimeout()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var response = await client.Http.SendAsync(Get("/api/ui/slow"));

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout, "the 1 s test override reaches the forwarder");
    }

    [Fact]
    public async Task BrokenApiConnection_Is502()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var response = await client.Http.SendAsync(Get("/api/ui/abort"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task HubWithoutWebSocket_Is400_AndNothingIsForwarded()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var (response, forwarded) = await ObserveAsync(() => client.Http.SendAsync(Get("/hubs/ui")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        forwarded.Should().BeEmpty();
    }

    private async Task<(WebSocketState State, HttpStatusCode? Status, string? Received)> ConnectHubAsync(
        BffClient client, string? origin, string? forwardedProto = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = client.Cookies;
        socket.Options.CollectHttpResponseDetails = true;
        if (origin is not null)
            socket.Options.SetRequestHeader("Origin", origin);
        if (forwardedProto is not null)
            socket.Options.SetRequestHeader("X-Forwarded-Proto", forwardedProto);
        var url = new UriBuilder(_factory.ServerAddress) { Scheme = "ws", Path = "/hubs/ui" }.Uri;
        try
        {
            await socket.ConnectAsync(url, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            return (socket.State, socket.HttpStatusCode, null);
        }

        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return (socket.State, socket.HttpStatusCode, Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    [Fact]
    public async Task HubUpgrade_IsForwarded_WithTheHubToken_AndNoCookie()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var ((_, status, received), forwarded) = await ObserveAsync(() => ConnectHubAsync(client, _factory.Origin));

        status.Should().Be(HttpStatusCode.SwitchingProtocols);
        received.Should().Be(StubPipelineApi.HubGreeting);
        var upstream = forwarded.Should().ContainSingle().Subject;
        upstream.WebSocket.Should().BeTrue();
        upstream.PathAndQuery.Should().Be("/hubs/ui");
        upstream.Headers.Should().NotContainKey("Cookie");
        var identity = await ValidateAsync(upstream.Headers["Authorization"]);
        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-reader");
        identity.FindFirst(UserTokenClaimTypes.Use)?.Value.Should().Be(UserTokenClaimTypes.HubUse);
    }

    [Fact]
    public async Task HubUpgrade_ThroughTraefik_WithForwardedProtoWss_IsForwarded()
    {
        // Traefik forwards the upgrade with X-Forwarded-Proto: wss while the page's Origin is https.
        using var client = await _factory.SignInClientAsync("reader");
        var httpsOrigin = new UriBuilder(_factory.ServerAddress) { Scheme = Uri.UriSchemeHttps }.Uri.GetLeftPart(UriPartial.Authority);

        var ((_, status, received), forwarded) = await ObserveAsync(() => ConnectHubAsync(client, httpsOrigin, "wss"));

        status.Should().Be(HttpStatusCode.SwitchingProtocols);
        received.Should().Be(StubPipelineApi.HubGreeting);
        forwarded.Should().ContainSingle().Which.WebSocket.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public async Task HubUpgrade_WithAForeignOrMissingOrigin_Is403(string? origin)
    {
        using var client = await _factory.SignInClientAsync("reader");

        var ((_, status, _), forwarded) = await ObserveAsync(() => ConnectHubAsync(client, origin));

        status.Should().Be(HttpStatusCode.Forbidden);
        forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task HubUpgrade_WithoutASession_Is401()
    {
        using var client = _factory.Anonymous();

        var ((_, status, _), forwarded) = await ObserveAsync(() => ConnectHubAsync(client, _factory.Origin));

        status.Should().Be(HttpStatusCode.Unauthorized);
        forwarded.Should().BeEmpty();
    }
}
