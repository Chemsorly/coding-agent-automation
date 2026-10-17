using CodingAgent.Pipeline;
using CodingAgent.Web.Auth;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace CodingAgent.Web.Bff;

/// <summary>
/// Spec 052 DD5: the web host forwards exactly <c>/api/ui/{**}</c> and <c>/hubs/ui</c> to the one
/// fixed destination <c>PipelineApi:BaseUrl</c>, with YARP direct forwarding (Req 2.1). The browser's
/// only credential is the session cookie; the forwarder swaps it for a user token minted per request
/// (Req 2.2), so no token ever reaches the browser (Req 2.3). Statuses and bodies pass through; YARP
/// answers 502 when the API cannot be reached and 504 when <see cref="ActivityTimeout"/> runs out.
/// </summary>
internal static class BffForwarding
{
    /// <summary>
    /// At least the client's 5-minute chat timeout (D11, Req 2.8). YARP's default of 100 s would cut a
    /// chat launch short. On the hub's WebSocket it is the idle timeout; SignalR's 15 s keep-alive
    /// keeps an open connection busy, so it has no lifetime limit.
    /// </summary>
    internal static readonly TimeSpan ActivityTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Test-only override of <see cref="ActivityTimeout"/> (a <see cref="TimeSpan"/>, e.g. <c>00:00:01</c>).</summary>
    internal const string ActivityTimeoutKey = "Bff:ActivityTimeout";

    public const string ApiPattern = "/api/ui/{**rest}";

    public static IServiceCollection AddBffForwarding(this IServiceCollection services)
    {
        services.AddHttpForwarder();
        return services;
    }

    public static WebApplication MapBffForwarders(this WebApplication app, string apiBaseUrl)
    {
        var config = new ForwarderRequestConfig
        {
            ActivityTimeout = app.Configuration.GetValue<TimeSpan?>(ActivityTimeoutKey) ?? ActivityTimeout,
        };

        app.MapForwarder(ApiPattern, apiBaseUrl, config, b => b.AddBffTransforms(hub: false))
            .RequireAuthorization(AccessPolicies.AnyAccess);
        app.MapForwarder(HubRoutes.Ui, apiBaseUrl, config, b => b.AddBffTransforms(hub: true))
            .RequireAuthorization(AccessPolicies.AnyAccess);
        return app;
    }

    private static void AddBffTransforms(this TransformBuilderContext builder, bool hub)
    {
        // The browser's credentials and the CSRF marker stay here; the API gets only the user token.
        builder.AddRequestHeaderRemove(HeaderNames.Authorization);
        builder.AddRequestHeaderRemove("X-Api-Key");
        builder.AddRequestHeaderRemove(HeaderNames.Cookie);
        builder.AddRequestHeaderRemove(BffRequestGuard.CsrfHeader);
        builder.AddRequestTransform(async context =>
        {
            var minter = context.HttpContext.RequestServices.GetRequiredService<UserTokenMinter>();
            var token = await minter.MintAsync(context.HttpContext.User, hub, context.CancellationToken);
            if (token is null)
            {
                // Cannot happen after the AnyAccess policy; refuse rather than forward without a token.
                // Setting the status makes YARP answer instead of forwarding.
                context.HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.ProxyRequest.Headers.Authorization = new("Bearer", token.Value);
        });

        builder.AddResponseHeaderRemove(HeaderNames.SetCookie, ResponseCondition.Always);
        builder.AddResponseTransform(context =>
        {
            // API answers are per user: never cached unless the API says otherwise.
            if (!context.HttpContext.Response.Headers.ContainsKey(HeaderNames.CacheControl))
                context.HttpContext.Response.Headers.CacheControl = "no-store";
            return ValueTask.CompletedTask;
        });
    }
}
