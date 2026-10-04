using System.Net;
using System.Text.RegularExpressions;
using CodingAgent.Web.IntegrationTests.Helpers;

namespace CodingAgent.Web.IntegrationTests.Smoke;

[Collection("SmokeTests")]
public partial class PageSmokeTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    // The rendered <base href> value.
    [GeneratedRegex("<base href=\"([^\"]*)\"")]
    private static partial Regex BaseHrefPattern();

    // The src/href of every <script src> and <link rel="stylesheet" href> in the page.
    [GeneratedRegex("<(?:link rel=\"stylesheet\" href|script src)=\"([^\"]+)\"")]
    private static partial Regex AssetReferencePattern();

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

    /// <summary>
    /// Loading a route directly must resolve the page's relative asset URLs (stylesheets, faro-init.js,
    /// _framework/blazor.web.js) from the app root. A fixed &lt;base href="./"&gt; resolved against the
    /// document URL, so a directly loaded /runs/{id} requested /runs/css/app.css etc. (all 404) and
    /// rendered unstyled and non-interactive.
    /// </summary>
    [Theory]
    [InlineData("/overview")]
    [InlineData("/runs")]
    [InlineData("/runs/00000000-0000-0000-0000-000000000000")]
    public async Task Get_Page_Resolves_Assets_From_App_Root(string path)
    {
        var documentUri = new Uri(_client.BaseAddress!, path);
        var html = await _client.GetStringAsync(documentUri);

        var baseHref = BaseHrefPattern().Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(baseHref), "The page must render a <base href>.");
        var baseUri = new Uri(documentUri, baseHref);
        Assert.Equal(new Uri(_client.BaseAddress!, "/"), baseUri);

        var assets = AssetReferencePattern().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Where(href => !Uri.TryCreate(href, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            .ToList();
        Assert.Contains("css/app.css", assets);
        Assert.Contains("_framework/blazor.web.js", assets);

        foreach (var asset in assets)
        {
            var assetUri = new Uri(baseUri, asset);
            var response = await _client.GetAsync(assetUri);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{path}: asset '{asset}' resolved to {assetUri.AbsolutePath} and returned {(int)response.StatusCode}.");
        }
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
