using CodingAgent.Contracts.Auth;

namespace CodingAgent.Api.Ui;

/// <summary>
/// The signed-in user of a UI-surface request and the roles their token grants (spec 051 D3).
/// Handlers call <see cref="DemandRead"/> or <see cref="DemandOperate"/> with the project of the
/// stored object they act on; a client-sent project ID is only trusted when creating something.
/// </summary>
public interface IUserAccess
{
    AccessGrant Grant { get; }

    /// <summary>The stable user ID (token <c>sub</c>).</summary>
    string? Subject { get; }

    string? Username { get; }

    string? IdentitySource { get; }

    bool CanRead(string? projectId);

    bool CanOperate(string? projectId);

    /// <summary>Throws <see cref="UiAccessDeniedException"/> (404) unless the user can read the project.</summary>
    void DemandRead(string? projectId);

    /// <summary>
    /// Throws <see cref="UiAccessDeniedException"/> unless the user can operate on the project:
    /// 404 when they cannot even read it, 403 when they can read but not operate.
    /// </summary>
    void DemandOperate(string? projectId);
}

/// <summary>Reads the user and grant from the request's user-token principal.</summary>
public sealed class UserAccess(IHttpContextAccessor httpContextAccessor) : IUserAccess
{
    private AccessGrant? _grant;

    private System.Security.Claims.ClaimsPrincipal User =>
        httpContextAccessor.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal();

    public AccessGrant Grant => _grant ??= User.Identity?.IsAuthenticated == true
        ? AccessGrantClaims.FromClaims(User.Claims)
        : AccessGrant.None;

    public string? Subject => User.FindFirst(UserTokenClaimTypes.Subject)?.Value;

    public string? Username => User.FindFirst(UserTokenClaimTypes.Username)?.Value;

    public string? IdentitySource => User.FindFirst(UserTokenClaimTypes.IdentitySource)?.Value;

    public bool CanRead(string? projectId) => Grant.RoleFor(projectId) >= AccessRole.ReadOnly;

    public bool CanOperate(string? projectId) => Grant.RoleFor(projectId) >= AccessRole.Operator;

    public void DemandRead(string? projectId)
    {
        if (!CanRead(projectId))
            throw new UiAccessDeniedException(StatusCodes.Status404NotFound);
    }

    public void DemandOperate(string? projectId)
    {
        DemandRead(projectId);
        if (!CanOperate(projectId))
            throw new UiAccessDeniedException(StatusCodes.Status403Forbidden);
    }
}

/// <summary>
/// A UI-surface request touched a project the user may not see (404, so the object's existence is
/// not revealed) or may see but not change (403). Mapped to the status by <see cref="UiAccessDeniedFilter"/>.
/// </summary>
public sealed class UiAccessDeniedException(int statusCode)
    : Exception(statusCode == StatusCodes.Status404NotFound ? "Not found." : "Forbidden.")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Turns a <see cref="UiAccessDeniedException"/> from a UI handler into its status code.</summary>
public sealed class UiAccessDeniedFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UiAccessDeniedException denied)
        {
            return Results.StatusCode(denied.StatusCode);
        }
    }
}
