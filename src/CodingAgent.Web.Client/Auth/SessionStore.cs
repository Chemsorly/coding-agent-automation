using System.Net;
using System.Net.Http.Json;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 052 DD8, Req 3.1–3.4: the browser's one copy of <c>GET /bff/session</c>. It loads the session
/// at start and again every <see cref="HeartbeatInterval"/> (the heartbeat). A 401 from the session
/// endpoint means the session is gone, whatever the reason (expiry, logout in another tab, a new key),
/// so the store sends the browser to the login page. A changed grant or expiry raises
/// <see cref="Changed"/>, and the authentication state follows. A network error keeps the last session:
/// the next heartbeat tries again.
/// </summary>
public sealed class SessionStore(
    IHttpClientFactory httpClients,
    LoginNavigator login,
    TimeProvider time,
    ILogger<SessionStore> logger) : IDisposable
{
    public const string ClientName = "bff-session";

    /// <summary>The session path, relative to the app's base address.</summary>
    public const string Path = "bff/session";

    /// <summary>How often an open page checks its session (Req 3.4: at the login page within a minute).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private Task<BffSession?>? _firstLoad;

    /// <summary>The last session the web host confirmed, or null before the first load and after it ended.</summary>
    public BffSession? Current { get; private set; }

    /// <summary>Raised when the identity, the grant or the expiry changed.</summary>
    public event Action? Changed;

    /// <summary>The first load, started once; the heartbeat starts after it.</summary>
    public Task<BffSession?> LoadAsync() => _firstLoad ??= FirstLoadAsync();

    /// <summary>
    /// One immediate check outside the heartbeat (Req 3.3: a proxied call got 401). True when the
    /// session is still valid; on a 401 the store has already started the navigation to the login page.
    /// </summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct = default) => await RefreshAsync(ct) is not null;

    private async Task<BffSession?> FirstLoadAsync()
    {
        var session = await RefreshAsync(_stopping.Token);
        _ = HeartbeatAsync();
        return session;
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token) && !login.IsNavigating)
                await RefreshAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    private async Task<BffSession?> RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var response = await httpClients.CreateClient(ClientName).GetAsync(Path, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Current = null;
                login.Go();
                return null;
            }
            response.EnsureSuccessStatusCode();

            var session = await response.Content.ReadFromJsonAsync(UiJsonContext.Default.BffSession, ct)
                ?? throw new InvalidOperationException("The session endpoint returned no content.");
            var changed = !SameAccess(Current, session);
            Current = session;
            if (changed)
                Changed?.Invoke();
            return session;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The session check failed; keeping the last session");
            return Current;
        }
    }

    /// <summary>Same user, expiry and grant (the records hold collections, so record equality does not apply).</summary>
    internal static bool SameAccess(BffSession? a, BffSession? b) =>
        a is not null && b is not null
        && a.Subject == b.Subject
        && a.Expires == b.Expires
        && a.Grant.GlobalRole == b.Grant.GlobalRole
        && a.Grant.ProjectRoles.Count == b.Grant.ProjectRoles.Count
        && a.Grant.ProjectRoles.All(p => b.Grant.ProjectRoles.TryGetValue(p.Key, out var role) && role == p.Value)
        && a.Groups.SequenceEqual(b.Groups, StringComparer.Ordinal);

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
