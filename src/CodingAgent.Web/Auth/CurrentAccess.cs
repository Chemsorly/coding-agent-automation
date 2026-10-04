using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// The signed-in principal and its evaluated <see cref="AccessGrant"/> for one Blazor circuit
/// (scoped). Components use it to decide synchronously what to render; mutations are enforced
/// separately by <see cref="IAccessGuard"/>. <c>CockpitLayout</c> initializes it before it renders
/// any page, so pages can read it without awaiting.
/// </summary>
public sealed class CurrentAccess : IDisposable
{
    private readonly AuthenticationStateProvider? _authenticationState;
    private readonly IRbacEvaluator? _rbac;
    private Task? _initialization;

    public CurrentAccess(AuthenticationStateProvider authenticationState, IRbacEvaluator rbac)
    {
        _authenticationState = authenticationState;
        _rbac = rbac;
        _authenticationState.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    private CurrentAccess(ClaimsPrincipal user, AccessGrant grant)
    {
        User = user;
        Grant = grant;
        IsReady = true;
        _initialization = Task.CompletedTask;
    }

    /// <summary>An already evaluated instance (component tests).</summary>
    internal static CurrentAccess CreateLoaded(ClaimsPrincipal user, AccessGrant grant) => new(user, grant);

    /// <summary>Raised after the grant was (re-)evaluated, e.g. when the session expired.</summary>
    public event Action? Changed;

    public bool IsReady { get; private set; }

    public ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());

    public AccessGrant Grant { get; private set; } = AccessGrant.None;

    public AccessRole GlobalRole => Grant.GlobalRole;

    public bool IsAdmin => GlobalRole == AccessRole.Admin;

    /// <summary>Global <c>readonly</c> or higher: Fleet, Consolidation, cross-project widgets.</summary>
    public bool HasGlobalRead => GlobalRole >= AccessRole.ReadOnly;

    public bool HasGlobalOperate => GlobalRole >= AccessRole.Operator;

    public string? Username => AuthPrincipals.GetUsername(User);

    public bool CanRead(string? projectId) => Grant.RoleFor(projectId) >= AccessRole.ReadOnly;

    public bool CanRead(Guid? projectId) => CanRead(ProjectIds.Normalize(projectId));

    public bool CanOperate(string? projectId) => Grant.RoleFor(projectId) >= AccessRole.Operator;

    public bool CanOperate(Guid? projectId) => CanOperate(ProjectIds.Normalize(projectId));

    /// <summary>Evaluates the principal once; later calls return the same task.</summary>
    public Task InitializeAsync() => _initialization ??= LoadAsync();

    private async Task LoadAsync()
    {
        var state = await _authenticationState!.GetAuthenticationStateAsync();
        User = state.User;
        Grant = await _rbac!.EvaluateAsync(User);
        IsReady = true;
        Changed?.Invoke();
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> _)
    {
        _initialization = LoadAsync();
    }

    public void Dispose()
    {
        if (_authenticationState is not null)
            _authenticationState.AuthenticationStateChanged -= OnAuthenticationStateChanged;
    }
}
