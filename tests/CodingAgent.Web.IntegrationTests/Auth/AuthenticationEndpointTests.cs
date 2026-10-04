using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>Spec 049 Req 1, 2, 10: login, redirects and anonymous endpoints of the web host.</summary>
[Collection("SmokeTests")]
public class AuthenticationEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthenticationEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    internal static bool SetsSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("ca_session", StringComparison.Ordinal));

    [Theory]
    [InlineData("/overview", "/overview")]
    [InlineData("/runs?page=2", "/runs?page=2")]
    [InlineData("/settings", "/settings")]
    public async Task UnauthenticatedPage_RedirectsToLoginWithReturnUrl(string path, string expectedReturnUrl)
    {
        var response = await NoRedirectClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith("http://localhost/login?returnUrl=");
        Uri.UnescapeDataString(location[(location.IndexOf('=') + 1)..]).Should().Be(expectedReturnUrl);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public async Task HealthEndpoints_AreAnonymous(string path)
    {
        var response = await NoRedirectClient().GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoginPage_IsAnonymous_AndShowsPasswordForm()
    {
        var response = await NoRedirectClient().GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-testid=\"login-form\"");
        html.Should().Contain("__RequestVerificationToken");
        html.Should().NotContain("data-testid=\"login-oidc\"", "OIDC is not configured in this factory");
    }

    [Fact]
    public async Task Login_Success_SetsSessionCookie_AndRedirectsToReturnUrl()
    {
        var client = NoRedirectClient();

        var response = await AuthTestEnvironment.PostLoginAsync(client, "admin", AuthTestEnvironment.AdminPassword, "/runs?page=2");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/runs?page=2");
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("ca_session") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        (await client.GetAsync("/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_BehindTlsIngress_SetsSecureSessionCookie()
    {
        var client = NoRedirectClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.40");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await AuthTestEnvironment.PostLoginAsync(client, "admin", AuthTestEnvironment.AdminPassword, "/runs");

        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("ca_session") && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("/\\evil.example")]
    [InlineData("/login?returnUrl=/overview")]
    [InlineData("/auth/logout")]
    public async Task Login_NonLocalOrAuthReturnUrl_LandsOnOverview(string returnUrl)
    {
        var response = await AuthTestEnvironment.PostLoginAsync(NoRedirectClient(), "admin", AuthTestEnvironment.AdminPassword, returnUrl);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/overview");
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("root", AuthTestEnvironment.AdminPassword)]
    [InlineData("", "")]
    public async Task Login_WrongCredentials_ShowsGenericError_AndDoesNotSignIn(string username, string password)
    {
        var client = NoRedirectClient();

        var response = await AuthTestEnvironment.PostLoginAsync(client, username, password, "/work");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login?error=credentials&returnUrl=%2Fwork");
        SetsSessionCookie(response).Should().BeFalse("a failed login must not issue a session");
        (await client.GetAsync("/overview")).StatusCode.Should().Be(HttpStatusCode.Redirect);

        var page = await client.GetStringAsync("/login?error=credentials");
        page.Should().Contain("Invalid username or password");
    }

    [Fact]
    public async Task LoginPage_NeverEchoesTheErrorValue()
    {
        var page = await NoRedirectClient().GetStringAsync("/login?error=%3Cscript%3Ealert(1)%3C/script%3E");

        page.Should().NotContain("<script>alert(1)");
        page.Should().NotContain("data-testid=\"login-error\"");
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        var response = await NoRedirectClient().PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = AuthTestEnvironment.AdminPassword,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SetsSessionCookie(response).Should().BeFalse();
    }

    [Fact]
    public async Task LoginPage_WhenSignedIn_RedirectsToTheLandingPage()
    {
        var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);

        var response = await client.GetAsync("/login?returnUrl=%2Fruns");

        // The fixed landing page, not the return URL: no redirect target comes from the query here.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/overview");
    }

    [Fact]
    public async Task BlazorHub_Unauthenticated_IsRejectedWithoutRedirect()
    {
        var response = await NoRedirectClient().PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BlazorHub_SignedIn_Negotiates()
    {
        var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);

        var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExportEndpoint_KeepsAgentApiKeyPolicy()
    {
        var anonymous = await NoRedirectClient().GetAsync("/api/export/runs.json");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the agent key scheme challenges with 401, not a login redirect");

        var withKey = NoRedirectClient();
        withKey.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");
        (await withKey.GetAsync("/api/export/runs.json")).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProfilePage_ShowsTheLocalAdmin()
    {
        var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);

        var html = await client.GetStringAsync("/user");

        html.Should().Contain("data-testid=\"profile-source\">Local admin<");
        html.Should().Contain("data-testid=\"profile-global-role\">admin<");
    }

    [Fact]
    public async Task Logout_ClearsTheSession()
    {
        var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);
        var token = await AuthTestEnvironment.GetAntiforgeryTokenAsync(client, "/user");

        var response = await client.PostAsync("/auth/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/login");
        (await client.GetAsync("/user")).StatusCode.Should().Be(HttpStatusCode.Redirect, "the session cookie is gone");
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_IsRejected_AndKeepsTheSession()
    {
        var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);

        var response = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/user")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OidcEndpoint_WhenOidcDisabled_Returns404()
    {
        (await NoRedirectClient().GetAsync("/auth/oidc")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>A factory whose login rate limit is 2 attempts per minute.</summary>
public sealed class RateLimitedWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        AuthTestEnvironment.Apply(loginRateLimitPerMinute: 2);
    }
}

[Collection("SmokeTests")]
public class LoginRateLimitTests : IClassFixture<RateLimitedWebApplicationFactory>
{
    private readonly RateLimitedWebApplicationFactory _factory;

    public LoginRateLimitTests(RateLimitedWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ThirdAttemptWithinAMinute_IsRejected_WithRateLimitMessage()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await AuthTestEnvironment.PostLoginAsync(client, "admin", "wrong-1");
        await AuthTestEnvironment.PostLoginAsync(client, "admin", "wrong-2");
        var third = await AuthTestEnvironment.PostLoginAsync(client, "admin", AuthTestEnvironment.AdminPassword);

        third.StatusCode.Should().Be(HttpStatusCode.Redirect);
        third.Headers.Location!.ToString().Should().Be("/login?error=ratelimit");
        AuthenticationEndpointTests.SetsSessionCookie(third).Should().BeFalse("a rate-limited attempt must not sign in, even with the right password");

        (await client.GetStringAsync("/login?error=ratelimit")).Should().Contain("Too many login attempts");
    }

    [Fact]
    public async Task ForwardedClientIp_PartitionsTheLimit()
    {
        // Behind the ingress every request comes from an ingress pod; X-Forwarded-For names the client.
        var first = ForwardedClient("203.0.113.10");
        await AuthTestEnvironment.PostLoginAsync(first, "admin", "wrong-1");
        await AuthTestEnvironment.PostLoginAsync(first, "admin", "wrong-2");
        var third = await AuthTestEnvironment.PostLoginAsync(first, "admin", "wrong-3");

        var other = await AuthTestEnvironment.PostLoginAsync(ForwardedClient("203.0.113.20"), "admin", "wrong-1");

        third.Headers.Location!.ToString().Should().Be("/login?error=ratelimit");
        other.Headers.Location!.ToString().Should().StartWith("/login?error=credentials", "another client keeps its own budget");
    }

    [Fact]
    public async Task ForwardedClientIp_UsesOnlyTheEntryTheIngressAdded()
    {
        // A client-supplied entry comes first; the ingress appends the address it saw.
        var spoofing = ForwardedClient("198.51.100.1, 203.0.113.30");
        await AuthTestEnvironment.PostLoginAsync(spoofing, "admin", "wrong-1");
        await AuthTestEnvironment.PostLoginAsync(spoofing, "admin", "wrong-2");

        var rotated = await AuthTestEnvironment.PostLoginAsync(ForwardedClient("198.51.100.2, 203.0.113.30"), "admin", "wrong-3");

        rotated.Headers.Location!.ToString().Should().Be("/login?error=ratelimit", "changing the client-supplied entry must not reset the budget");
    }

    private HttpClient ForwardedClient(string forwardedFor)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }
}

/// <summary>Local admin disabled, OIDC enabled (never contacted: no challenge is issued).</summary>
public sealed class OidcOnlyWebApplicationFactory : CustomWebApplicationFactory
{
    private static readonly string[] Variables =
        ["Auth__Admin__Enabled", "Auth__Oidc__Enabled", "Auth__Oidc__Name", "Auth__Oidc__Issuer", "Auth__Oidc__ClientId", "Auth__Oidc__ClientSecret"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("Auth__Admin__Enabled", "false");
        Environment.SetEnvironmentVariable("Auth__Oidc__Enabled", "true");
        Environment.SetEnvironmentVariable("Auth__Oidc__Name", "Entra ID");
        Environment.SetEnvironmentVariable("Auth__Oidc__Issuer", "https://idp.invalid/tenant/v2.0");
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientId", "client");
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientSecret", "secret");
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

[Collection("SmokeTests")]
public class AdminDisabledTests : IClassFixture<OidcOnlyWebApplicationFactory>
{
    private readonly OidcOnlyWebApplicationFactory _factory;

    public AdminDisabledTests(OidcOnlyWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LoginPage_OffersOnlyTheIdentityProvider()
    {
        var html = await _factory.CreateClient().GetStringAsync("/login");

        html.Should().Contain("Sign in with Entra ID");
        html.Should().NotContain("data-testid=\"login-form\"");
    }

    [Fact]
    public async Task PasswordLogin_IsNotAccepted()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = AuthTestEnvironment.AdminPassword,
        }));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        AuthenticationEndpointTests.SetsSessionCookie(response).Should().BeFalse();
    }
}
