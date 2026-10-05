using System.Net;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>
/// A provider that answers the OIDC handler's backchannel in place of the network. Its discovery
/// document advertises pushed authorization requests, like Keycloak's does.
/// </summary>
public sealed class StubOidcProvider(StubOidcProvider.Behavior behavior) : HttpMessageHandler
{
    public const string Issuer = "https://idp.invalid/realms/test";
    private const string ParEndpoint = Issuer + "/protocol/openid-connect/ext/par/request";

    public enum Behavior
    {
        /// <summary>Discovery fails, as when the provider is unreachable.</summary>
        Unreachable,

        /// <summary>
        /// Every pushed authorization request is rejected with the response Keycloak sent in
        /// production for an unknown client.
        /// </summary>
        RejectsPushedAuthorization,
    }

    private int _pushedAuthorizationRequests;

    public int PushedAuthorizationRequests => _pushedAuthorizationRequests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (behavior == Behavior.Unreachable)
            throw new HttpRequestException("Name or service not known (idp.invalid:443)");

        var uri = request.RequestUri!.ToString();
        if (uri == Issuer + "/.well-known/openid-configuration")
        {
            return Task.FromResult(Json(HttpStatusCode.OK, $$"""
                {
                  "issuer": "{{Issuer}}",
                  "authorization_endpoint": "{{Issuer}}/protocol/openid-connect/auth",
                  "token_endpoint": "{{Issuer}}/protocol/openid-connect/token",
                  "pushed_authorization_request_endpoint": "{{ParEndpoint}}"
                }
                """));
        }

        if (uri == ParEndpoint && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref _pushedAuthorizationRequests);
            return Task.FromResult(Json(HttpStatusCode.Unauthorized,
                """{"error":"invalid_request","error_description":"Authentication failed."}"""));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>Local admin and OIDC ("Keycloak") enabled, the provider replaced by a <see cref="StubOidcProvider"/>.</summary>
public abstract class StubOidcProviderWebApplicationFactory(StubOidcProvider.Behavior behavior) : CustomWebApplicationFactory
{
    private static readonly string[] Variables =
        ["Auth__Oidc__Enabled", "Auth__Oidc__Name", "Auth__Oidc__Issuer", "Auth__Oidc__ClientId", "Auth__Oidc__ClientSecret"];

    public StubOidcProvider Provider { get; } = new(behavior);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("Auth__Oidc__Enabled", "true");
        Environment.SetEnvironmentVariable("Auth__Oidc__Name", "Keycloak");
        Environment.SetEnvironmentVariable("Auth__Oidc__Issuer", StubOidcProvider.Issuer);
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientId", "coding-agent");
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientSecret", "secret");

        // Configure (not PostConfigure): the handler's post-configure builds the backchannel from it.
        builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(OidcRegistration.OidcScheme, o => o.BackchannelHttpHandler = Provider));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var variable in Variables)
                Environment.SetEnvironmentVariable(variable, null);
        }
        base.Dispose(disposing);
    }
}

public sealed class ParRejectingWebApplicationFactory()
    : StubOidcProviderWebApplicationFactory(StubOidcProvider.Behavior.RejectsPushedAuthorization);

public sealed class UnreachableProviderWebApplicationFactory()
    : StubOidcProviderWebApplicationFactory(StubOidcProvider.Behavior.Unreachable);

/// <summary>
/// A failure while starting the OIDC sign-in lands on the login page with the provider error,
/// like a failure at the callback, instead of an empty 500.
/// </summary>
[Collection("SmokeTests")]
public class OidcChallengeRejectedTests : IClassFixture<ParRejectingWebApplicationFactory>
{
    private readonly ParRejectingWebApplicationFactory _factory;

    public OidcChallengeRejectedTests(ParRejectingWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ProviderRejectsThePushedAuthorizationRequest_LandsOnLoginWithError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var pushedBefore = _factory.Provider.PushedAuthorizationRequests;

        var response = await client.GetAsync("/auth/oidc?returnUrl=%2Fruns");

        _factory.Provider.PushedAuthorizationRequests.Should().Be(pushedBefore + 1, "PAR stays enabled when the provider advertises it");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=oidc");
        AuthenticationEndpointTests.SetsSessionCookie(response).Should().BeFalse();
        (await client.GetStringAsync(response.Headers.Location)).Should().Contain("Sign-in with Keycloak failed");
    }
}

[Collection("SmokeTests")]
public class OidcProviderUnreachableTests : IClassFixture<UnreachableProviderWebApplicationFactory>
{
    private readonly UnreachableProviderWebApplicationFactory _factory;

    public OidcProviderUnreachableTests(UnreachableProviderWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DiscoveryFails_LandsOnLoginWithError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/auth/oidc");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=oidc");
    }
}
