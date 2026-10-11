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
}
