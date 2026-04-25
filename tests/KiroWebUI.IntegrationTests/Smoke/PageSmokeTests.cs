using System.Net;

namespace KiroWebUI.IntegrationTests.Smoke;

public class PageSmokeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PageSmokeTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/agent-coding")]
    [InlineData("/settings")]
    [InlineData("/about")]
    public async Task Get_Page_Returns_Success(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
