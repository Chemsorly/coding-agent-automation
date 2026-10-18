using CodingAgent.Web.Auth;
using CodingAgent.Web.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web;

/// <summary>
/// Spec 052 DD2: the browser's services, on top of the pages' own (<see cref="WebClientServiceCollectionExtensions.AddWebClientServices"/>).
/// The UI clients call the web host's origin, which forwards to the API (the BFF); the session cookie is
/// the only credential. The DI test builds exactly this container.
/// </summary>
public static class WebClientHost
{
    public static IServiceCollection AddBrowserHostServices(this IServiceCollection services, Uri baseAddress)
    {
        services.AddWebClientServices(baseAddress.ToString());
        services.AddSingleton<IUiAccessTokenSource, NoAccessToken>();
        services.AddSingleton(TimeProvider.System);

        // The session, its heartbeat and the version check (DD8).
        services.AddSingleton<LoginNavigator>();
        services.AddSingleton<VersionWatch>();
        services.AddSingleton<SessionStore>();
        services.AddTransient<BffHttpHandler>();
        services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<BffHttpHandler>());
        services.AddHttpClient(SessionStore.ClientName, client => client.BaseAddress = baseAddress);

        // Authentication and access from the session (Req 3.2); page policies for in-app navigation (Req 1.6).
        services.AddScoped<AuthenticationStateProvider, BffAuthenticationStateProvider>();
        services.AddScoped<IRbacEvaluator, SessionRbacEvaluator>();
        services.AddScoped<IAuthUiInfo, SessionAuthUiInfo>();
        services.AddTransient(sp => sp.GetRequiredService<SessionStore>().Current?.Build ?? new BuildInfo());
        services.AddCascadingAuthenticationState();
        services.AddAuthorizationCore(options => AccessPolicyRegistration.Register(options, scheme: null));
        services.AddScoped<IAuthorizationHandler, AccessAuthorizationHandler>();
        return services;
    }
}
