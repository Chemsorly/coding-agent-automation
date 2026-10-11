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

        return services;
    }
}
