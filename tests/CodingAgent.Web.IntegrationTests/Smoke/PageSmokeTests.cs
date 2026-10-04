using System.Net;
using CodingAgent.Web.IntegrationTests.Helpers;

namespace CodingAgent.Web.IntegrationTests.Smoke;

[Collection("SmokeTests")]
public class PageSmokeTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = default!;
    private HttpClient _clientNoRedirect = default!;

    public PageSmokeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Spec 049: pages require a signed-in user.
        _client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory);
        _clientNoRedirect = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
