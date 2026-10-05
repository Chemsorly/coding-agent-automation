using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>Security headers, HSTS and the antiforgery cookie of the web host.</summary>
[Collection("SmokeTests")]
public class SecurityHeadersTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityHeadersTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>A client for a host other than localhost, which HSTS always skips.</summary>
    private HttpClient IngressClient(string? forwardedProto)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://coding-agent.example"),
        });
        if (forwardedProto is not null)
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
        return client;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        string.Join(", ", response.Headers.TryGetValues(name, out var values) ? values : []);

    [Theory]
    [InlineData("/login")]
    [InlineData("/overview")]
    [InlineData("/css/app.css")]
    [InlineData("/healthz")]
    public async Task EveryResponse_SendsTheSecurityHeaders(string path)
    {
        var response = await IngressClient("https").GetAsync(path);

        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Be(SecurityHeadersRegistration.ContentSecurityPolicy);
        Header(response, "X-Content-Type-Options").Should().Be("nosniff");
        Header(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Header(response, "Permissions-Policy").Should().Be(SecurityHeadersRegistration.PermissionsPolicy);
        Header(response, "Cross-Origin-Opener-Policy").Should().Be("same-origin");
        Header(response, "Cross-Origin-Resource-Policy").Should().Be("same-origin");
    }

    [Fact]
    public async Task Hsts_IsSentOverHttps()
    {
        var response = await IngressClient("https").GetAsync("/login");

        Header(response, "Strict-Transport-Security").Should().StartWith("max-age=");
    }

    [Theory]
    [InlineData("http")]
    [InlineData(null)]
    public async Task Hsts_IsNotSentOverHttp(string? forwardedProto)
    {
        var response = await IngressClient(forwardedProto).GetAsync("/login");

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }

    [Fact]
    public async Task AntiforgeryCookie_BehindTlsIngress_IsSecure()
    {
        var response = await IngressClient("https").GetAsync("/login");

        response.Headers.GetValues("Set-Cookie").Should().Contain(c =>
            c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpsRedirect_IsOffByDefault()
    {
        var response = await IngressClient("http").GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>A factory with the HTTPS redirect on, as the chart renders it for an ingress with tls.</summary>
public sealed class HttpsRedirectWebApplicationFactory : CustomWebApplicationFactory
{
    private const string Variable = "WebUI__HttpsRedirect";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable(Variable, "true");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Environment.SetEnvironmentVariable(Variable, null);
        base.Dispose(disposing);
    }
}

[Collection("SmokeTests")]
public class HttpsRedirectTests : IClassFixture<HttpsRedirectWebApplicationFactory>
{
    private readonly HttpsRedirectWebApplicationFactory _factory;

    public HttpsRedirectTests(HttpsRedirectWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client(string baseAddress, params (string Name, string Value)[] headers)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(baseAddress),
        });
        foreach (var (name, value) in headers)
            client.DefaultRequestHeaders.Add(name, value);
        return client;
    }

    [Fact]
    public async Task PlainHttpThroughTheIngress_RedirectsToTheSameUrlOverHttps()
    {
        var client = Client("http://coding-agent.example:8080", ("X-Forwarded-Proto", "http"), ("X-Forwarded-For", "203.0.113.50"));

        var response = await client.GetAsync("/runs?page=2");

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location!.ToString().Should().Be("https://coding-agent.example/runs?page=2");
    }

    [Fact]
    public async Task PlainHttpLoginPost_IsRedirectedBeforeItIsProcessed()
    {
        var client = Client("http://coding-agent.example", ("X-Forwarded-Proto", "http"));

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = AuthTestEnvironment.AdminPassword,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location!.ToString().Should().Be("https://coding-agent.example/auth/login");
        AuthenticationEndpointTests.SetsSessionCookie(response).Should().BeFalse();
    }

    [Fact]
    public async Task HttpsThroughTheIngress_IsServed()
    {
        var response = await Client("http://coding-agent.example", ("X-Forwarded-Proto", "https")).GetAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RequestWithoutForwardedScheme_IsServed()
    {
        // kubectl port-forward, probes and in-cluster calls reach the pod without the ingress.
        var direct = await Client("http://localhost").GetAsync("/login");
        var forwardedForOnly = await Client("http://localhost", ("X-Forwarded-For", "203.0.113.51")).GetAsync("/login");

        direct.StatusCode.Should().Be(HttpStatusCode.OK);
        forwardedForOnly.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
