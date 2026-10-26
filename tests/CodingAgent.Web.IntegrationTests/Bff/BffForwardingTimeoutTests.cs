using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.Bff;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>
/// Spec 052 Req 2, 9.1: the BFF returns 504 when <see cref="ShortTimeoutBffWebApplicationFactory"/>'s
/// 1 s activity timeout expires before the API responds.
/// </summary>
[Collection("SmokeTests")]
public class BffForwardingTimeoutTests : IClassFixture<ShortTimeoutBffWebApplicationFactory>
{
    private readonly ShortTimeoutBffWebApplicationFactory _factory;

    public BffForwardingTimeoutTests(ShortTimeoutBffWebApplicationFactory factory) => _factory = factory;

    private static HttpRequestMessage Get(string path, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (csrf)
            request.Headers.Add(BffRequestGuard.CsrfHeader, "1");
        return request;
    }

    [Fact]
    public async Task ApiTimeout_Is504_WithTheShortTestTimeout()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var response = await client.Http.SendAsync(Get("/api/ui/slow"));

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout, "the 1 s test override reaches the forwarder");
    }
}
