using System.Net;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

public class PageSmokeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PageSmokeTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/agent-coding")]
    [InlineData("/agent-monitoring")]
    [InlineData("/settings")]
    [InlineData("/about")]
    public async Task Get_Page_Returns_Success(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
