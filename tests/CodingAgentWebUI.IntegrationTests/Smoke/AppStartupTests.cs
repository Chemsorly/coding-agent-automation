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
        // Creating the client boots the entire app — if DI is broken, this throws
        using var client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_Returns_OK()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
