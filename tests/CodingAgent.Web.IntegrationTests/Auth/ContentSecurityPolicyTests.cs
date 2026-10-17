using System.Text.RegularExpressions;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>
/// Spec 052 Req 14.8 (the ZAP baseline's Medium findings): scripts only from the web host or with the
/// response's nonce, WebAssembly compilation, fonts from the web host, and Faro's CDN and collector only
/// when Faro is on. Styles keep 'unsafe-inline', because Blazor renders style attributes.
/// </summary>
[Collection("SmokeTests")]
public sealed partial class ContentSecurityPolicyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ContentSecurityPolicyTests(CustomWebApplicationFactory factory) => _factory = factory;

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceSource();

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>")]
    private static partial Regex InlineScriptTag();

    /// <summary>The nonce a response's policy names.</summary>
    internal static string NonceOf(string policy) => NonceSource().Match(policy).Groups[1].Value;

    /// <summary>The response carries the web host's policy, with its own nonce and without Faro (the tests configure none).</summary>
    internal static void ShouldBeTheWebHostPolicy(string policy)
    {
        NonceOf(policy).Should().NotBeEmpty("the policy names this response's nonce");
        policy.Should().Be(SecurityHeadersRegistration.ContentSecurityPolicy(NonceOf(policy), faroCollectorOrigin: null));
    }

    [Fact]
    public async Task HostPage_InlineScripts_CarryTheResponsesNonce_AndEveryResponseHasItsOwn()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await client.GetAsync("/login");
        var second = await client.GetAsync("/login");

        var nonce = NonceOf(first.Headers.GetValues("Content-Security-Policy").Single());
        var inline = InlineScriptTag().Matches(await first.Content.ReadAsStringAsync()).Select(m => m.Value).ToList();
        inline.Should().HaveCountGreaterThanOrEqualTo(3, "positive control: the import map and the two inline scripts of App.razor");
        inline.Should().OnlyContain(tag => tag.Contains($"nonce=\"{nonce}\""), "an inline script without the nonce would not run");
        NonceOf(second.Headers.GetValues("Content-Security-Policy").Single()).Should().NotBe(nonce, "a nonce is used once");
    }

    [Fact]
    public void Policy_WithoutFaro_AllowsOnlyTheWebHost()
    {
        var policy = SecurityHeadersRegistration.ContentSecurityPolicy("n0nce", faroCollectorOrigin: null);

        Directives(policy).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["default-src"] = "'self'",
            ["script-src"] = "'self' 'nonce-n0nce' 'wasm-unsafe-eval'",
            ["style-src"] = "'self' 'unsafe-inline'",
            ["img-src"] = "'self'",
            ["font-src"] = "'self'",
            ["connect-src"] = "'self'",
            ["base-uri"] = "'self'",
            ["form-action"] = "'self'",
            ["frame-ancestors"] = "'self'",
            ["object-src"] = "'none'",
        });
    }

    [Fact]
    public void Policy_WithFaro_AddsItsCdnForScripts_AndItsCollectorForCalls()
    {
        var policy = Directives(SecurityHeadersRegistration.ContentSecurityPolicy("n0nce", "https://faro.example.test"));

        policy["script-src"].Should().Be("'self' 'nonce-n0nce' 'wasm-unsafe-eval' " + SecurityHeadersRegistration.FaroScriptOrigin);
        policy["connect-src"].Should().Be("'self' https://faro.example.test");
    }

    [Theory]
    [InlineData("https://faro-collector-prod-eu-west-0.grafana.net/collect/0123abc", "https://faro-collector-prod-eu-west-0.grafana.net")]
    [InlineData("https://faro.example.test:8443/collect", "https://faro.example.test:8443")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("faro.example.test/collect", null)]
    [InlineData("ftp://faro.example.test/collect", null)]
    public void FaroCollectorOrigin_IsTheSchemeHostAndPort_OfAnAbsoluteHttpUrl(string? url, string? expected)
    {
        SecurityHeadersRegistration.FaroCollectorOrigin(url).Should().Be(expected);
    }

    private static Dictionary<string, string> Directives(string policy) =>
        policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(d => d[0], d => d[1]);
}
