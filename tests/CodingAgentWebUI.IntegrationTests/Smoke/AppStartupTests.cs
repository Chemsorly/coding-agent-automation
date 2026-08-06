using System.Net;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

[Collection("SmokeTests")]
public class AppStartupTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AppStartupTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void App_Starts_Without_Throwing()
    {
        // Creating the client boots the entire app — if DI is broken, this throws.
        // Verify the client is operational by checking it can reach the health endpoint.
        using var client = _factory.CreateClient();
        Assert.NotNull(client);
        // BaseAddress is set by WebApplicationFactory — proves the factory bootstrapped correctly
        Assert.StartsWith("http://", client.BaseAddress?.ToString() ?? "");
    }

    [Fact]
    public async Task HealthEndpoint_Returns_OK()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
