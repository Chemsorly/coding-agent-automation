using CodingAgent.Web.Auth;
using CodingAgent.Web.Ui;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Web;

/// <summary>
/// Spec 051 D6: the pages call the Pipeline API's UI surface (<c>/api/ui/**</c>, <c>/hubs/ui</c>) as the signed-in
/// user, with a token this host mints per DI scope (<see cref="UserTokenProvider"/>), never with the master API
/// key. The clients and page services themselves are <see cref="WebClientServiceCollectionExtensions.AddWebClientServices"/>.
/// </summary>
internal static class UiClientRegistration
{
    public static IServiceCollection AddUiClients(this IServiceCollection services, string apiBaseUrl, SecurityKey userTokenSigningKey)
    {
        services.AddSingleton(new UserTokenSigningKey(userTokenSigningKey));
        services.AddScoped<IUiAccessTokenSource, UserTokenProvider>();
        return services.AddWebClientServices(apiBaseUrl);
    }
}
