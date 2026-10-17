using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using CodingAgent.Web.IntegrationTests.Helpers;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.IntegrationTests.Smoke;

/// <summary>
/// Spec 052 Req 1.5, 1.6, 5.3, 9.6: a full page load of any route serves the host page that loads the
/// WebAssembly client; the page itself renders in the browser (its content is bUnit's and E2E's). It
/// replaces the 051 page smoke tests, which asserted on prerendered HTML.
/// </summary>
[Collection("SmokeTests")]
public partial class HostPageTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    /// <summary>The marker of a WebAssembly root component; a static page may still carry component state.</summary>
    private const string WebAssemblyMarker = "<!--Blazor:{\"type\":\"webassembly\"";

    // The rendered <base href> value.
    [GeneratedRegex("<base href=\"([^\"]*)\"")]
    private static partial Regex BaseHrefPattern();

    // The src/href of every <script src> and <link rel="stylesheet" href> in the page.
    [GeneratedRegex("<(?:link rel=\"stylesheet\" href|script src)=\"([^\"]+)\"")]
    private static partial Regex AssetReferencePattern();

    // The fingerprinted framework entry point, e.g. _framework/blazor.web.9hsif5t8mt.js.
    [GeneratedRegex(@"^_framework/blazor\.web(\.[a-z0-9]+)?\.js$")]
    private static partial Regex BlazorScriptPattern();

    // The _framework URLs of the import map, e.g. "./_framework/dotnet.abc123.js".
    [GeneratedRegex("\"(?:\\./)?(_framework/[^\"]+\\.js)\"")]
    private static partial Regex ImportMapFrameworkPattern();

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = default!;
    private HttpClient _clientNoRedirect = default!;

    public HostPageTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Spec 049: pages require a signed-in user.
        _client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory, allowAutoRedirect: false);
        _clientNoRedirect = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Every routable page of the client, with sample values for its route parameters.</summary>
    public static TheoryData<string> ClientPages()
    {
        var data = new TheoryData<string>();
        foreach (var template in typeof(CodingAgent.Web.Components.Routes).Assembly.GetTypes()
                     .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
                     .Select(r => r.Template)
                     .Distinct()
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Regex.Replace(template, "\\{[^}]+\\}", Guid.Empty.ToString()));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ClientPages))]
    public async Task SignedIn_FullPageLoad_ServesTheHostPage(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(WebAssemblyMarker, html);
        Assert.Contains("id=\"app-loading\"", html);
        Assert.Contains("id=\"blazor-error-ui\"", html);
        Assert.True(response.Headers.CacheControl?.NoCache == true && response.Headers.CacheControl.NoStore,
            $"{path}: the host page must not be cached (Req 5.3), got '{response.Headers.CacheControl}'.");
    }

    [Theory]
    [MemberData(nameof(ClientPages))]
    public async Task Anonymous_FullPageLoad_RedirectsToLogin(string path)
    {
        var response = await _clientNoRedirect.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    /// <summary>
    /// Loading a route directly must resolve the page's relative asset URLs (stylesheets, faro-init.js,
    /// the fingerprinted blazor.web.js) from the app root. A fixed &lt;base href="./"&gt; resolved against
    /// the document URL, so a directly loaded /runs/{id} requested /runs/css/app.css etc. (all 404).
    /// The WebAssembly runtime files of the import map are served too (V3).
    /// </summary>
    [Theory]
    [InlineData("/overview")]
    [InlineData("/runs")]
    [InlineData("/runs/00000000-0000-0000-0000-000000000000")]
    [InlineData("/pipelines/00000000-0000-0000-0000-000000000000")]
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
        Assert.Contains(assets, a => BlazorScriptPattern().IsMatch(a));

        var runtime = ImportMapFrameworkPattern().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains(runtime, r => r.StartsWith("_framework/dotnet.", StringComparison.Ordinal));

        foreach (var asset in assets.Concat(runtime))
        {
            var assetUri = new Uri(baseUri, asset);
            var response = await _client.GetAsync(assetUri);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{path}: asset '{asset}' resolved to {assetUri.AbsolutePath} and returned {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// The favicon is needed before sign-in (the login page) and by dashboards that load it without
    /// a session, so it must be linked from the shell and served anonymously.
    /// </summary>
    [Fact]
    public async Task Favicon_Is_Linked_And_Served_Without_Sign_In()
    {
        var html = await _clientNoRedirect.GetStringAsync("/login");
        Assert.Contains("<link rel=\"icon\" type=\"image/svg+xml\" href=\"favicon.svg\"", html);

        var response = await _clientNoRedirect.GetAsync("/favicon.svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The login page stays a static server-rendered form (Req 1.3): no WebAssembly marker.</summary>
    [Fact]
    public async Task LoginPage_RendersStatically()
    {
        var html = await _clientNoRedirect.GetStringAsync("/login");

        Assert.Contains("data-testid=\"login-form\"", html);
        Assert.DoesNotContain(WebAssemblyMarker, html);
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
