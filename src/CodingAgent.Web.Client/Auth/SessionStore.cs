using System.Net;
using System.Net.Http.Json;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 052 DD8, Req 3.1–3.4: the browser's one copy of <c>GET /bff/session</c>. It loads the session
/// at start and again every <see cref="HeartbeatInterval"/> (the heartbeat). A 401 from the session
/// endpoint means the session is gone, whatever the reason (expiry, logout in another tab, a new key),
/// so the store sends the browser to the login page. A changed grant or expiry raises
/// <see cref="Changed"/>, and the authentication state follows. A network error keeps the last session:
/// the next heartbeat tries again. A browser may run a hidden tab's timers only once a minute, so the
/// store also checks at once when the tab comes back into view (<see cref="OnPageVisibleAsync"/>).
/// </summary>
public sealed class SessionStore(
    IHttpClientFactory httpClients,
    LoginNavigator login,
    TimeProvider time,
    IJSRuntime js,
    ILogger<SessionStore> logger) : IDisposable
{
    /// <summary>The host page's function that calls <see cref="OnPageVisibleAsync"/> when the tab becomes visible.</summary>
    public const string WatchPageVisibilityFunction = "watchPageVisibility";

    public const string ClientName = "bff-session";

    /// <summary>The session path, relative to the app's base address.</summary>
    public const string Path = "bff/session";

    /// <summary>How often an open page checks its session (Req 3.4: at the login page within a minute).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private Task<BffSession?>? _firstLoad;
    private DotNetObjectReference<SessionStore>? _self;

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

    /// <summary>The tab came back into view (Req 3.4): check the session now instead of at the next heartbeat.</summary>
    [JSInvokable]
    public async Task OnPageVisibleAsync()
    {
        if (!login.IsNavigating)
            await RefreshAsync(_stopping.Token);
    }

    private async Task<BffSession?> FirstLoadAsync()
    {
        var session = await RefreshAsync(_stopping.Token);
        _ = HeartbeatAsync();
        await WatchPageVisibilityAsync();
        return session;
    }

    private async Task WatchPageVisibilityAsync()
    {
        try
        {
            _self = DotNetObjectReference.Create(this);
            await js.InvokeVoidAsync(WatchPageVisibilityFunction, _self);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // The heartbeat still runs; only the check on returning to the tab is missing.
            logger.LogWarning(ex, "The page visibility check could not be registered");
        }
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
        _self?.Dispose();
    }
}
