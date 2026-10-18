using System.Security.Claims;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Components.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 052 Req 3.2: the browser's authentication state, built from <see cref="SessionStore"/> with the
/// same <see cref="AuthPrincipals.Create"/> the web host uses, so pages see the same principal as before.
/// Authentication state is never serialized into the page (D5).
/// </summary>
public sealed class BffAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly SessionStore _store;

    public BffAuthenticationStateProvider(SessionStore store)
    {
        _store = store;
        _store.Changed += OnSessionChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync() =>
        new(ToPrincipal(await _store.GetCurrentAsync()));

    internal static ClaimsPrincipal ToPrincipal(BffSession? session) =>
        session is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : AuthPrincipals.Create(session.IdentitySource, session.Subject, session.Username, session.Email,
                session.DisplayName, session.Groups, session.Expires);

    private void OnSessionChanged() =>
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(ToPrincipal(_store.Current))));

    public void Dispose() => _store.Changed -= OnSessionChanged;
}

/// <summary>Spec 052 D5: the grant the web host evaluated; bindings are never evaluated in the browser.</summary>
public sealed class SessionRbacEvaluator(SessionStore store) : IRbacEvaluator
{
    public async Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true)
            return AccessGrant.None;
        return (await store.GetCurrentAsync())?.Grant ?? AccessGrant.None;
    }
}

/// <summary>The sign-in details the pages show, from the session (spec 051 5.2).</summary>
public sealed class SessionAuthUiInfo(SessionStore store) : IAuthUiInfo
{
    public string OidcProviderName => store.Current?.OidcProviderName ?? "";
}

/// <summary>
/// Spec 052 DD9: in the browser the session cookie is the only credential; the web host attaches the
/// user token when it forwards. The clients keep their token seam for the API contract tests.
/// </summary>
public sealed class NoAccessToken : IUiAccessTokenSource
{
    public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
}
