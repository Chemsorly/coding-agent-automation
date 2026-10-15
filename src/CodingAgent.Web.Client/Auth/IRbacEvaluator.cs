using System.Security.Claims;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Maps an authenticated principal to its roles (Spec 049 Req 5). The server host evaluates the configured
/// bindings (<c>RbacEvaluator</c>); a WebAssembly host would read them from <c>/api/ui/me</c> (spec 051 5.2).
/// </summary>
public interface IRbacEvaluator
{
    Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default);
}
