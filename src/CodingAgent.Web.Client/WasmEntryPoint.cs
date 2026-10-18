using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CodingAgent.Web.Client;

/// <summary>
/// Spec 052 DD1: the browser app's entry point. A namespaced class, not top-level statements, because
/// a global <c>Program</c> would clash with the web host's <c>public partial class Program</c> in the
/// test projects that see both. There are no root components: the web host's markers start
/// <c>Routes</c>.
/// </summary>
public static class WasmEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);

        // Component logs reach the browser console from Information up (Req 6.3); framework categories
        // only from Warning, so IHttpClientFactory writes nothing per request. Faro forwards only
        // console warnings and errors (faro-init.js).
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        builder.Services.AddBrowserHostServices(new Uri(builder.HostEnvironment.BaseAddress));

        var host = builder.Build();
        var appVersion = await host.Services.GetRequiredService<IJSRuntime>().InvokeAsync<string?>("getAppVersion");
        host.Services.GetRequiredService<VersionWatch>().SetBoot(appVersion);
        await host.RunAsync();
    }
}
