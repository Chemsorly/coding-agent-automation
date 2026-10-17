using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Hosting;

namespace CodingAgent.Web.IntegrationTests.Security;

/// <summary>
/// The web host in the Production environment, as it is deployed. The test default is Development,
/// where <c>MapStaticAssets</c> adds a fallback endpoint for files outside the build manifest.
/// </summary>
public sealed class ProductionWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}
