using System.Security.Claims;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Components.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 051 D2: the user token for the Pipeline API's UI surface, for one DI scope (a Blazor circuit
/// or a prerender request). <see cref="UserTokenMinter"/> mints it from the scope's signed-in user;
/// this provider keeps it until shortly before it expires or the user changes. A scope only ever
/// hands out its own user's token. Spec 052 deletes it with the circuits.
/// </summary>
internal sealed class UserTokenProvider(
    AuthenticationStateProvider authenticationState,
    UserTokenMinter minter,
    TimeProvider time) : IUiAccessTokenSource
{
    /// <summary>A token this close to expiry is replaced, so it does not run out on the way to the API.</summary>
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private Minted _http;
    private Minted _hub;

    public async ValueTask<string?> GetTokenAsync(CancellationToken ct = default)
    {
        _http = await GetAsync(_http, hub: false, ct);
        return _http.Token;
    }

    public async ValueTask<string?> GetHubTokenAsync(CancellationToken ct = default)
    {
        _hub = await GetAsync(_hub, hub: true, ct);
        return _hub.Token;
    }

    /// <summary>The <paramref name="current"/> token while it is the same user's and not about to expire, else a new one.</summary>
    private async Task<Minted> GetAsync(Minted current, bool hub, CancellationToken ct)
    {
        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        if (current.Token is not null && ReferenceEquals(user, current.User) && time.GetUtcNow() < current.Expires - RenewBefore)
            return current;

        var minted = await minter.MintAsync(user, hub, ct);
        return new Minted(user, minted?.Value, minted?.Expires ?? default);
    }

    private readonly record struct Minted(ClaimsPrincipal? User, string? Token, DateTimeOffset Expires);
}
