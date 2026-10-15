namespace CodingAgent.Web.Ui;

/// <summary>
/// The credential of the UI clients (spec 051 D9): the bearer token of the current user for the
/// Pipeline API's UI surface (<c>/api/ui/**</c>), or null when there is none. The server host mints
/// one per DI scope from the signed-in user. A WebAssembly host returns null and lets the
/// session cookie authenticate its calls to the web host instead.
/// </summary>
public interface IUiAccessTokenSource
{
    ValueTask<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// The token the UI hub connects with (spec 051 D2, D7). It lasts until the session ends, so a long-lived
    /// connection is not dropped every few minutes; the hub closes it when the session ends. Unless a host
    /// mints a separate one, it is the same token.
    /// </summary>
    ValueTask<string?> GetHubTokenAsync(CancellationToken ct = default) => GetTokenAsync(ct);
}
