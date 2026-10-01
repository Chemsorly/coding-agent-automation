using System.Net;

namespace CodingAgent.Web.IntegrationTests.Smoke;

[Collection("SmokeTests")]
public class PageSmokeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly HttpClient _clientNoRedirect;

    public PageSmokeTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _clientNoRedirect = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("/agent-coding")]
    [InlineData("/overview")]
    [InlineData("/work")]
    [InlineData("/fleet")]
    [InlineData("/runs")]
    [InlineData("/settings")]
    [InlineData("/about")]
    [InlineData("/attention")]
    [InlineData("/insights")]
    [InlineData("/knowledge")]
    [InlineData("/pipelines")]
    [InlineData("/consolidation")]
    [InlineData("/agent-chat")]
    public async Task Get_Page_Returns_Success(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_Root_Redirects_To_Overview()
    {
        // "/" should redirect to "overview" so the Overview nav item is highlighted on landing.
        // The redirect target is a bare relative URL ("overview"), not "/overview" — this is
        // consistent with Results.Redirect("overview") which produces a relative Location header.
        var response = await _clientNoRedirect.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.Contains("overview", location, StringComparison.OrdinalIgnoreCase);
    }
}
