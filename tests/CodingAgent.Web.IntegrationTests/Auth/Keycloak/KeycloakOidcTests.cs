using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using CodingAgent.Web.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Auth.Keycloak;

/// <summary>
/// Spec 049 Req 3 and 12.4: OIDC login against a real Keycloak, end to end through the web host.
/// Runs only where Docker is available (CI job <c>iam-tests</c>, or locally with Docker Desktop).
/// In Docker-less environments (e.g. the local agent quality-gate runner) each test returns early
/// via <see cref="KeycloakFixture.IsDockerUnavailable"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Feature", "IAM")]
[Collection("SmokeTests")]
public class KeycloakOidcTests : IClassFixture<KeycloakFixture>
{
    private readonly KeycloakFixture _fixture;

    public KeycloakOidcTests(KeycloakFixture fixture) => _fixture = fixture;

    private OidcFlowDriver Driver() => new(_fixture.Factory!);

    [RequiresDockerFact]
    public async Task OperatorOfOneProject_LogsIn_WithGroupsAndProjectRole()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();

        var callback = await driver.LoginAsync("alice", "alice-password");

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().Be("/test/whoami");
        AuthenticationEndpointTests.SetsSessionCookie(callback).Should().BeTrue();

        var me = await driver.WhoAmIAsync();
        me.Should().NotBeNull();
        me!.Username.Should().Be("alice");
        me.IdentitySource.Should().Be("oidc");
        me.Groups.Should().Equal("team-a");
        me.GlobalRole.Should().Be("none");
        me.ProjectRoles.Should().Equal(new Dictionary<string, string> { [KeycloakFixture.PaymentsProjectId] = "operator" });
    }

    [RequiresDockerFact]
    public async Task AdminGroupMember_IsGlobalAdmin()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();
        await driver.LoginAsync("bob", "bob-password");

        var me = await driver.WhoAmIAsync();

        me!.Groups.Should().Equal("platform-team");
        me.GlobalRole.Should().Be("admin");
    }

    [RequiresDockerFact]
    public async Task UserWithoutGroups_IsSignedIn_WithoutAnyRole()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();
        await driver.LoginAsync("carol", "carol-password");

        var me = await driver.WhoAmIAsync();

        me!.Username.Should().Be("carol");
        me.Groups.Should().BeEmpty();
        me.GlobalRole.Should().Be("none");
        me.ProjectRoles.Should().BeEmpty();
    }

    /// <summary>
    /// Keycloak advertises pushed authorization requests, so the handler sends the code-flow and
    /// PKCE parameters over the back channel and the browser redirect carries only a request_uri.
    /// The test realm requires PKCE S256 for the client, so every successful login in this class
    /// also proves the handler sends it.
    /// </summary>
    [RequiresDockerFact]
    public async Task AuthorizeRedirect_UsesPushedAuthorizationRequest_ToTheConfiguredIssuer()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();
        (await driver.LoginAsync("carol", "carol-password")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        driver.AuthorizeUrl!.ToString().Should().StartWith(_fixture.Issuer);
        var query = System.Web.HttpUtility.ParseQueryString(driver.AuthorizeUrl.Query);
        query["client_id"].Should().Be(KeycloakFixture.ClientId);
        query["request_uri"].Should().StartWith("urn:ietf:params:oauth:request_uri:");
        query["code_challenge"].Should().BeNull("PKCE travels in the pushed request, not in the browser URL");
    }

    [RequiresDockerFact]
    public async Task WrongPassword_StaysOnKeycloak_AndCreatesNoSession()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();

        var response = await driver.LoginAsync("alice", "not-her-password");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Keycloak shows its login form again");
        (await driver.WhoAmIAsync()).Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task TamperedCallback_LandsOnLoginWithError()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();
        await driver.App.GetAsync("/auth/oidc"); // sets the correlation cookie

        var response = await driver.App.GetAsync("/signin-oidc?state=forged&code=forged");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=oidc");
        (await driver.App.GetStringAsync("/login?error=oidc")).Should().Contain("Sign-in with Keycloak failed");
    }

    /// <summary>
    /// Keycloak rejects the pushed authorization request of a client it does not know. That
    /// failure happens while starting the sign-in, before any redirect to Keycloak.
    /// </summary>
    [RequiresDockerFact]
    public async Task UnknownClient_PushedRequestRejected_LandsOnLoginWithError()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var factory = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(OidcRegistration.OidcScheme, o => o.ClientId = "unknown-client")));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var response = await client.GetAsync("/auth/oidc?returnUrl=%2Ftest%2Fwhoami");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=oidc");
        (await client.GetStringAsync(response.Headers.Location)).Should().Contain("Sign-in with Keycloak failed");
    }

    [RequiresDockerFact]
    public async Task LoginPage_OffersKeycloakAndThePasswordForm()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();

        var html = await driver.App.GetStringAsync("/login");

        html.Should().Contain("data-testid=\"login-oidc\"").And.Contain("Sign in with Keycloak");
        html.Should().Contain("data-testid=\"login-form\"");
    }

    [RequiresDockerFact]
    public async Task Logout_EndsTheOidcSession()
    {
        if (_fixture.IsDockerUnavailable) return;
        using var driver = Driver();
        await driver.LoginAsync("alice", "alice-password");
        var token = await AuthTestEnvironment.GetAntiforgeryTokenAsync(driver.App, "/user");

        var logout = await driver.App.PostAsync("/auth/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        logout.Headers.Location!.ToString().Should().Be("/login");
        (await driver.WhoAmIAsync()).Should().BeNull();
    }
}
