using AwesomeAssertions;
using CodingAgent.Web;

namespace CodingAgent.Web.UnitTests;

/// <summary>
/// Verifies the &lt;base href&gt; App.razor renders. A fixed relative base of "./" resolved against
/// the current document URL, so a directly loaded nested route such as /runs/{id} requested
/// /runs/css/app.css and /runs/_framework/blazor.web.js (all 404) and rendered unstyled and inert.
/// </summary>
public class BaseHrefResolverTests
{
    private const string RootDocumentOrigin = "http://localhost:18090";

    // Rancher's service proxy strips this prefix before forwarding, so the app sees only "/runs/{id}".
    private const string RancherProxyPrefix =
        "https://rancher.example.com/k8s/clusters/c-xxxxx/api/v1/namespaces/coding-agent/services/http:coding-agent-web:80/proxy";

    [Theory]
    [InlineData(null, "./")]
    [InlineData("", "./")]
    [InlineData("/", "./")]
    [InlineData("/overview", "./")]
    [InlineData("/runs", "./")]
    [InlineData("/runs/", "../")]
    [InlineData("/runs/3f2b7c1e-0000-0000-0000-000000000000", "../")]
    [InlineData("/runs/abc/steps", "../../")]
    public void Resolve_DefaultConfiguration_ClimbsFromRouteToAppRoot(string? requestPath, string expected)
    {
        BaseHrefResolver.Resolve(configuredBasePath: null, requestPath).Should().Be(expected);
    }

    [Theory]
    [InlineData("./")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_HelmDefaultOrBlank_IsTreatedAsAppRoot(string configured)
    {
        BaseHrefResolver.Resolve(configured, "/runs/abc").Should().Be("../");
        BaseHrefResolver.Resolve(configured, "/overview").Should().Be("./");
    }

    [Theory]
    [InlineData("/k8s/clusters/c-xxxxx/proxy/", "/k8s/clusters/c-xxxxx/proxy/")]
    [InlineData("/k8s/clusters/c-xxxxx/proxy", "/k8s/clusters/c-xxxxx/proxy/")]
    [InlineData("/", "/")]
    [InlineData("https://rancher.example.com/app", "https://rancher.example.com/app/")]
    public void Resolve_RootedOrAbsoluteConfiguration_IsUsedAsIsWithTrailingSlash(string configured, string expected)
    {
        BaseHrefResolver.Resolve(configured, "/runs/abc").Should().Be(expected);
    }

    [Fact]
    public void Resolve_RelativeConfiguration_IsResolvedAgainstAppRoot()
    {
        BaseHrefResolver.Resolve("./ui", "/runs/abc").Should().Be("../ui/");
        BaseHrefResolver.Resolve("ui/", "/overview").Should().Be("./ui/");
    }

    [Theory]
    [InlineData("/overview")]
    [InlineData("/runs")]
    [InlineData("/runs/3f2b7c1e-0000-0000-0000-000000000000")]
    [InlineData("/runs/abc/steps")]
    public void Resolve_DefaultConfiguration_AssetsResolveFromAppRootAtRootDeployment(string requestPath)
    {
        var assets = ResolveAssets(RootDocumentOrigin, requestPath);

        assets.Should().Equal(
            $"{RootDocumentOrigin}/css/app.css",
            $"{RootDocumentOrigin}/_framework/blazor.web.js");
    }

    [Theory]
    [InlineData("/overview")]
    [InlineData("/runs/3f2b7c1e-0000-0000-0000-000000000000")]
    public void Resolve_DefaultConfiguration_AssetsResolveUnderPrefixStrippingProxy(string requestPath)
    {
        var assets = ResolveAssets(RancherProxyPrefix, requestPath);

        assets.Should().Equal(
            $"{RancherProxyPrefix}/css/app.css",
            $"{RancherProxyPrefix}/_framework/blazor.web.js");
    }

    /// <summary>
    /// Resolves the asset URLs App.razor references the way a browser does: the base href against
    /// the document URL, then each relative asset against the base.
    /// </summary>
    private static string[] ResolveAssets(string documentOrigin, string requestPath)
    {
        var documentUri = new Uri(documentOrigin + requestPath);
        var baseUri = new Uri(documentUri, BaseHrefResolver.Resolve(configuredBasePath: null, requestPath));
        return [new Uri(baseUri, "css/app.css").ToString(), new Uri(baseUri, "_framework/blazor.web.js").ToString()];
    }
}
