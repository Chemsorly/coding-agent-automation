using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Bff;

/// <summary>A navigation manager for tests outside bUnit; records each navigation.</summary>
internal sealed class RecordingNavigationManager : NavigationManager
{
    public RecordingNavigationManager(string uri = "http://localhost/overview") => Initialize("http://localhost/", uri);

    public List<(string Uri, bool ForceLoad)> Navigations { get; } = [];

    protected override void NavigateToCore(string uri, NavigationOptions options) => Navigations.Add((uri, options.ForceLoad));
}

/// <summary>
/// Spec 052 Req 9.3: every service a Web.Client component injects is registered by the browser's own
/// container (<see cref="WebClientHost.AddBrowserHostServices"/>). This replaces the phase 0 test on the
/// server container, which no longer renders the pages.
/// </summary>
public class WebClientHostDiTests
{
    private static ServiceProvider BuildBrowserContainer()
    {
        var services = new ServiceCollection();
        // What WebAssemblyHostBuilder itself provides.
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new RecordingNavigationManager());
        services.AddSingleton(Mock.Of<IJSRuntime>());
        services.AddBrowserHostServices(new Uri("http://localhost/"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public async Task EveryComponentInjection_ResolvesFromTheBrowserContainer()
    {
        var injections = typeof(CockpitLayout).Assembly.GetTypes()
            .Where(t => typeof(ComponentBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
                .Select(p => (Component: t, p.PropertyType)))
            .ToList();
        injections.Should().NotBeEmpty();

        await using var provider = BuildBrowserContainer();
        await using var scope = provider.CreateAsyncScope();
        var failures = new List<string>();
        foreach (var (component, type) in injections.DistinctBy(i => i.PropertyType))
        {
            try
            {
                if (scope.ServiceProvider.GetService(type) is null)
                    failures.Add($"{type.FullName} (injected by {component.Name}): not registered");
            }
            catch (Exception ex)
            {
                failures.Add($"{type.FullName} (injected by {component.Name}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public async Task UiClients_CallTheWebHostsOrigin_WithoutAToken()
    {
        await using var provider = BuildBrowserContainer();

        provider.GetRequiredService<CodingAgent.Web.Ui.IUiAccessTokenSource>().Should().BeOfType<CodingAgent.Web.Auth.NoAccessToken>(
            "the session cookie is the browser's only credential; the BFF attaches the user token");
        (await provider.GetRequiredService<CodingAgent.Web.Ui.IUiAccessTokenSource>().GetTokenAsync()).Should().BeNull();
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(CodingAgent.Web.Auth.SessionStore.ClientName)
            .BaseAddress.Should().Be(new Uri("http://localhost/"));
    }
}
