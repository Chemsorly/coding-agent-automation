using CodingAgent.Web.Auth;
using CodingAgent.Web.Ui;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Web;

/// <summary>
/// Spec 051 D6: the typed clients of the Pipeline API's UI surface (<c>/api/ui/**</c>). They call
/// the API as the signed-in user, with a token minted per DI scope by <see cref="UserTokenProvider"/>,
/// never with the master API key.
/// </summary>
internal static class UiClientRegistration
{
    public static IServiceCollection AddUiClients(this IServiceCollection services, string apiBaseUrl, SecurityKey userTokenSigningKey)
    {
        services.AddSingleton(new UserTokenSigningKey(userTokenSigningKey));
        services.AddScoped<IUiAccessTokenSource, UserTokenProvider>();

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
