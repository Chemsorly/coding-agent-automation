using CodingAgent.Web.Auth;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web;

/// <summary>
/// Spec 051 5.2: the services of the pages, registered once for the server host and later for the WebAssembly
/// host. The host adds what differs between the two: the <see cref="IUiAccessTokenSource"/>, the
/// <see cref="IRbacEvaluator"/>, the <see cref="IAuthUiInfo"/> and the authentication state.
/// </summary>
public static class WebClientServiceCollectionExtensions
{
    /// <param name="apiBaseUrl">The Pipeline API (server host) or the web host's API proxy (WebAssembly host).</param>
    public static IServiceCollection AddWebClientServices(this IServiceCollection services, string apiBaseUrl)
    {
        // The signed-in user's access, for rendering (CurrentAccess) and for checks before an action (IAccessGuard).
        services.AddScoped<CurrentAccess>();
        services.AddScoped<IAccessGuard, AccessGuard>();

        // Page-level services, one instance per circuit.
        services.AddScoped<IFaroService, FaroService>();
        services.AddScoped<NotificationFaroBridge>();
        services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        services.AddScoped<DispatchDrawers>();
        services.AddScoped<AgentCodingPageService>();
        services.AddScoped<RepositoryPageService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<CockpitState>();
        services.AddScoped<BlockedIssuesService>();

        // One UI hub connection per circuit (spec 051 D7), with the user's session-long hub token.
        services.AddScoped<IUiHubConnection>(sp => new UiHubConnection(
            new Uri($"{apiBaseUrl.TrimEnd('/')}{CodingAgent.Pipeline.HubRoutes.Ui}"), sp.GetRequiredService<IUiAccessTokenSource>()));

        // The typed clients of the API's UI surface (/api/ui/**, spec 051 D6), as the signed-in user.
        services.AddHttpClient<IUiMeClient, UiMeClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiHealthClient, UiHealthClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiConfigClient, UiConfigClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiSettingsClient, UiSettingsClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiRunClient, UiRunClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiWorkItemClient, UiWorkItemClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiConsolidationClient, UiConsolidationClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        services.AddHttpClient<IUiLoopClient, UiLoopClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        // Creating several issues on a slow tracker can take longer than the default timeout. No retries:
        // the POSTs are not idempotent (a retried re-run would start a second attempt).
        services.AddHttpClient<IUiTriageClient, UiTriageClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        services.AddHttpClient<IUiAgentClient, UiAgentClient>(client => client.BaseAddress = new Uri(apiBaseUrl));
        // The backlog pages through every enabled issue provider and checks each issue's dependencies.
        services.AddHttpClient<IUiBrowseClient, UiBrowseClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        // A dispatch reads the issue, its comments and the provider configs before it enqueues.
        services.AddHttpClient<IUiDispatchClient, UiDispatchClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        // Launching a chat waits until the pod connects (ChatPodConnectTimeoutSeconds, 120 s by default),
        // so the client allows well above that and the API's own timeout answers first.
        services.AddHttpClient<IUiChatClient, UiChatClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        // Provider checks call GitHub or GitLab from the API, and fetching models runs a Job there.
        services.AddHttpClient<IUiProviderClient, UiProviderClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
        });


        return services;
    }
}
