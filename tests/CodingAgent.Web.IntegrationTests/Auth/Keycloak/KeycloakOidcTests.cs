using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Helpers;

namespace CodingAgent.Web.IntegrationTests.Auth.Keycloak;

/// <summary>
/// Spec 049 Req 3 and 12.4: OIDC login against a real Keycloak, end to end through the web host.
/// Runs only where Docker is available (CI job <c>iam-tests</c>, or locally with Docker Desktop).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Feature", "IAM")]
[Collection("SmokeTests")]
public class KeycloakOidcTests : IClassFixture<KeycloakFixture>
{
    private readonly KeycloakFixture _fixture;

    public KeycloakOidcTests(KeycloakFixture fixture) => _fixture = fixture;

    private OidcFlowDriver Driver() => new(_fixture.Factory);

    [DockerAvailableFact]
    public async Task OperatorOfOneProject_LogsIn_WithGroupsAndProjectRole()
    {
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

    [DockerAvailableFact]
    public async Task AdminGroupMember_IsGlobalAdmin()
    {
        using var driver = Driver();
        await driver.LoginAsync("bob", "bob-password");

        var me = await driver.WhoAmIAsync();

        me!.Groups.Should().Equal("platform-team");
        me.GlobalRole.Should().Be("admin");
    }

    [DockerAvailableFact]
    public async Task UserWithoutGroups_IsSignedIn_WithoutAnyRole()
    {
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
    [DockerAvailableFact]
    public async Task AuthorizeRedirect_UsesPushedAuthorizationRequest_ToTheConfiguredIssuer()
    {
        using var driver = Driver();
        (await driver.LoginAsync("carol", "carol-password")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        driver.AuthorizeUrl!.ToString().Should().StartWith(_fixture.Issuer);
        var query = System.Web.HttpUtility.ParseQueryString(driver.AuthorizeUrl.Query);
        query["client_id"].Should().Be(KeycloakFixture.ClientId);
        query["request_uri"].Should().StartWith("urn:ietf:params:oauth:request_uri:");
        query["code_challenge"].Should().BeNull("PKCE travels in the pushed request, not in the browser URL");
    }

    [DockerAvailableFact]
    public async Task WrongPassword_StaysOnKeycloak_AndCreatesNoSession()
    {
        using var driver = Driver();

        var response = await driver.LoginAsync("alice", "not-her-password");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Keycloak shows its login form again");
        (await driver.WhoAmIAsync()).Should().BeNull();
    }

    [DockerAvailableFact]
    public async Task TamperedCallback_LandsOnLoginWithError()
    {
        using var driver = Driver();
        await driver.App.GetAsync("/auth/oidc"); // sets the correlation cookie

        var response = await driver.App.GetAsync("/signin-oidc?state=forged&code=forged");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=oidc");
        (await driver.App.GetStringAsync("/login?error=oidc")).Should().Contain("Sign-in with Keycloak failed");
    }

    [DockerAvailableFact]
    public async Task LoginPage_OffersKeycloakAndThePasswordForm()
    {
        using var driver = Driver();

        var html = await driver.App.GetStringAsync("/login");

        html.Should().Contain("data-testid=\"login-oidc\"").And.Contain("Sign in with Keycloak");
        html.Should().Contain("data-testid=\"login-form\"");
    }

    [DockerAvailableFact]
    public async Task Logout_EndsTheOidcSession()
    {
        using var driver = Driver();
        await driver.LoginAsync("alice", "alice-password");
        var token = await AuthTestEnvironment.GetAntiforgeryTokenAsync(driver.App, "/user");

        var logout = await driver.App.PostAsync("/auth/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        logout.Headers.Location!.ToString().Should().Be("/login");
        (await driver.WhoAmIAsync()).Should().BeNull();
    }
}
