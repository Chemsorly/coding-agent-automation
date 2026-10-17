using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.Bff;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>
/// Spec 052 Req 14.7: the BFF's own responses carry the web host's security headers, once each: the session
/// endpoint (also never cached) and a response forwarded from the API.
/// </summary>
[Collection("SmokeTests")]
public class BffSecurityHeadersTests : IClassFixture<BffWebApplicationFactory>
{
    private readonly BffWebApplicationFactory _factory;

    public BffSecurityHeadersTests(BffWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/bff/session")]
    [InlineData("/api/ui/runs")]
    public async Task BffResponses_SendEverySecurityHeaderOnce_AndAreNotCached(string path)
    {
        using var client = await _factory.SignInClientAsync("reader");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(BffRequestGuard.CsrfHeader, "1");

        var response = await client.Http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Be(SecurityHeadersRegistration.ContentSecurityPolicy);
        foreach (var (name, value) in new[]
                 {
                     ("X-Content-Type-Options", "nosniff"),
                     ("Referrer-Policy", "strict-origin-when-cross-origin"),
                     ("Permissions-Policy", SecurityHeadersRegistration.PermissionsPolicy),
                     ("Cross-Origin-Opener-Policy", "same-origin"),
                     ("Cross-Origin-Resource-Policy", "same-origin"),
                 })
        {
            response.Headers.GetValues(name).Should().ContainSingle(name).Which.Should().Be(value, name);
        }
        response.Headers.CacheControl!.NoStore.Should().BeTrue("session and API data must not be cached");
    }
}
