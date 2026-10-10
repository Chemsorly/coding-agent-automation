using System.Net;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Testcontainers.Keycloak;

namespace CodingAgent.Web.IntegrationTests.Auth.Keycloak;

/// <summary>
/// A real Keycloak (Testcontainers) with the committed test realm, plus a web host configured
/// against it (Spec 049 Req 12.4, D14). Needs Docker: these tests run in the <c>iam-tests</c> CI job.
/// When Docker is absent <see cref="RequiresDockerFactAttribute"/> skips the tests before this
/// fixture is even initialized.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "coding-agent-test";
    public const string ClientId = "coding-agent";
    public const string ClientSecret = "test-client-secret";
    public const string PaymentsProjectId = "6f1c2a9e-0000-0000-0000-00000000000a";

    // Pinned like every other image in CI; bump deliberately.
    private const string KeycloakImage = "quay.io/keycloak/keycloak:26.3";

    // Null until InitializeAsync runs: building the container in a field initializer throws in
    // Docker-less environments before the test framework can apply trait filtering.
    private KeycloakContainer? _keycloak;

    /// <summary>True when Docker is unavailable; each test should return early when this is set.</summary>
    public bool IsDockerUnavailable => _keycloak is null;

    public string Issuer { get; private set; } = "";

    public OidcWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        KeycloakContainer container;
        try
        {
            container = new KeycloakBuilder()
                .WithImage(KeycloakImage)
                .WithResourceMapping(
                    new FileInfo(Path.Combine(AppContext.BaseDirectory, "Auth", "Keycloak", $"{Realm}-realm.json")),
                    "/opt/keycloak/data/import/")
                .WithCommand("--import-realm")
                .Build();
        }
        catch (Exception)
        {
            // Docker is unavailable; tests will skip via IsDockerUnavailable.
            return;
        }

        _keycloak = container;
        await _keycloak.StartAsync();
        Issuer = new Uri(new Uri(_keycloak.GetBaseAddress()), $"realms/{Realm}").ToString();
        Factory = new OidcWebApplicationFactory(Issuer);
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        if (_keycloak is not null)
            await _keycloak.DisposeAsync();
    }
}

/// <summary>
/// The smoke-test web host with OIDC pointed at the test realm, RBAC bindings for the realm's
/// groups, a project "payments", and a test-only <c>/test/whoami</c> endpoint that reports the
/// session claims and the evaluated grant.
/// </summary>
public sealed class OidcWebApplicationFactory(string issuer) : CustomWebApplicationFactory
{
    private static readonly string[] AuthVariables =
    [
        "Auth__Oidc__Enabled", "Auth__Oidc__Name", "Auth__Oidc__Issuer", "Auth__Oidc__ClientId", "Auth__Oidc__ClientSecret",
        "Auth__Rbac__Bindings__0__Group", "Auth__Rbac__Bindings__0__Role", "Auth__Rbac__Bindings__0__ProjectId",
        "Auth__Rbac__Bindings__1__Group", "Auth__Rbac__Bindings__1__Role",
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        Environment.SetEnvironmentVariable("Auth__Oidc__Enabled", "true");
        Environment.SetEnvironmentVariable("Auth__Oidc__Name", "Keycloak");
        Environment.SetEnvironmentVariable("Auth__Oidc__Issuer", issuer);
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientId", KeycloakFixture.ClientId);
        Environment.SetEnvironmentVariable("Auth__Oidc__ClientSecret", KeycloakFixture.ClientSecret);
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__0__Group", "team-a");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__0__Role", "operator");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__0__ProjectId", KeycloakFixture.PaymentsProjectId);
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__1__Group", "platform-team");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__1__Role", "admin");

        builder.ConfigureTestServices(services =>
        {
            // The test realm runs on plain http. Configure (not PostConfigure): the handler's own
            // post-configure validates the metadata address and runs before later post-configures.
            services.Configure<OpenIdConnectOptions>(OidcRegistration.OidcScheme, o => o.RequireHttpsMetadata = false);

            var projects = new Mock<IProjectStore>();
            projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new PipelineProject { Id = KeycloakFixture.PaymentsProjectId, Name = "payments" }]);
            services.RemoveAll<IProjectStore>();
            services.AddSingleton(projects.Object);

            services.AddSingleton<IStartupFilter, WhoAmIStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var variable in AuthVariables)
                Environment.SetEnvironmentVariable(variable, null);
        }
        base.Dispose(disposing);
    }

    /// <summary>Session claims and evaluated roles of the caller (401 without a session).</summary>
    public sealed record WhoAmI(
        string? Username, string? IdentitySource, string[] Groups, string GlobalRole, Dictionary<string, string> ProjectRoles);

    private sealed class WhoAmIStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/test/whoami", branch => branch.Run(async context =>
            {
                var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (!result.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var user = result.Principal!;
                var grant = await context.RequestServices.GetRequiredService<IRbacEvaluator>().EvaluateAsync(user);
                await context.Response.WriteAsJsonAsync(new WhoAmI(
                    AuthPrincipals.GetUsername(user),
                    user.FindFirst(AuthClaimTypes.IdentitySource)?.Value,
                    AuthPrincipals.GetGroups(user).ToArray(),
                    AccessRoleNames.ToName(grant.GlobalRole),
                    grant.ProjectRoles.ToDictionary(r => r.Key, r => AccessRoleNames.ToName(r.Value))));
            }));
            next(app);
        };
    }
}

/// <summary>
/// Drives the authorization code flow like a browser: the app through the test server client,
/// Keycloak through a real HTTP client, redirects followed by hand. Keycloak marks its session
/// cookies Secure, which an HttpClient cookie container never sends over plain http, so the
/// Keycloak cookies are kept by hand.
/// </summary>
public sealed partial class OidcFlowDriver : IDisposable
{
    private readonly HttpClient _keycloak = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Dictionary<string, string> _keycloakCookies = new(StringComparer.Ordinal);

    public OidcFlowDriver(WebApplicationFactory<Program> factory)
    {
        App = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    /// <summary>The app client; it holds the session cookie after a successful login.</summary>
    public HttpClient App { get; }

    /// <summary>The Keycloak authorize URL the app redirected to at the start of the last login.</summary>
    public Uri? AuthorizeUrl { get; private set; }

    /// <summary>Starts at <c>/auth/oidc</c>, submits the Keycloak login form, and follows the redirects back.</summary>
    /// <returns>The app's final response after the callback (a redirect to <paramref name="returnUrl"/> on success).</returns>
    public async Task<HttpResponseMessage> LoginAsync(string username, string password, string returnUrl = "/test/whoami")
    {
        var challenge = await App.GetAsync($"/auth/oidc?returnUrl={Uri.EscapeDataString(returnUrl)}");
        if (challenge.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Expected a challenge redirect, got {(int)challenge.StatusCode}");
        AuthorizeUrl = challenge.Headers.Location!;

        var loginPage = await SendToKeycloakAsync(new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl));
        var html = await loginPage.Content.ReadAsStringAsync();
        var action = LoginFormAction().Match(html);
        if (!action.Success)
            throw new InvalidOperationException($"No Keycloak login form ({(int)loginPage.StatusCode}): {html[..Math.Min(html.Length, 500)]}");

        var submit = await SendToKeycloakAsync(new HttpRequestMessage(HttpMethod.Post, WebUtility.HtmlDecode(action.Groups["action"].Value))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
                ["credentialId"] = "",
            }),
        });
        if (submit.StatusCode != HttpStatusCode.Found && submit.StatusCode != HttpStatusCode.Redirect)
            return submit; // Keycloak re-rendered the form (wrong password)

        return await App.GetAsync(submit.Headers.Location!);
    }

    public async Task<OidcWebApplicationFactory.WhoAmI?> WhoAmIAsync()
    {
        var response = await App.GetAsync("/test/whoami");
        return response.StatusCode == HttpStatusCode.OK
            ? await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<OidcWebApplicationFactory.WhoAmI>(response.Content)
            : null;
    }

    private async Task<HttpResponseMessage> SendToKeycloakAsync(HttpRequestMessage request)
    {
        if (_keycloakCookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", _keycloakCookies.Select(c => $"{c.Key}={c.Value}")));
        var response = await _keycloak.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';', 2)[0];
                var separator = pair.IndexOf('=');
                if (separator > 0)
                    _keycloakCookies[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
            }
        }
        return response;
    }

    [GeneratedRegex("""<form\b(?=[^>]*\bid="kc-form-login")[^>]*\baction="(?<action>[^"]+)""")]
    private static partial Regex LoginFormAction();

    public void Dispose()
    {
        _keycloak.Dispose();
        App.Dispose();
    }
}
