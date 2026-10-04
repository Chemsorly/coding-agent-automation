using System.Security.Claims;
using CodingAgent.Web.Auth;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>
/// Spec 049 access services for component tests. Components read <see cref="CurrentAccess"/>
/// synchronously (the layout initializes it in production), so tests register an already
/// evaluated instance. The default is a global admin, which renders every control as before.
/// </summary>
public static class TestAccess
{
    public static readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddHours(12);

    public static ClaimsPrincipal User(string username = "tester", params string[] groups) =>
        AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, email: null, displayName: null, groups, Expires);

    public static AccessGrant Global(AccessRole role) => new(role, new Dictionary<string, AccessRole>(), []);

    public static AccessGrant Scoped(params (string ProjectId, AccessRole Role)[] roles) =>
        new(AccessRole.None, roles.ToDictionary(r => ProjectIds.Normalize(r.ProjectId)!, r => r.Role), []);

    /// <summary>An evaluated <see cref="CurrentAccess"/> and a real <see cref="AccessGuard"/> over the same grant.</summary>
    public static (CurrentAccess Access, IAccessGuard Guard) Create(AccessGrant? grant = null, ClaimsPrincipal? user = null)
    {
        grant ??= Global(AccessRole.Admin);
        var access = CurrentAccess.CreateLoaded(user ?? User(), grant);
        return (access, new AccessGuard(access, new FixedRbacEvaluator(grant), NullLogger<AccessGuard>.Instance));
    }

    /// <summary>Registers <see cref="CurrentAccess"/>, <see cref="IAccessGuard"/> and what auth components need.</summary>
    public static IServiceCollection AddTestAccess(this IServiceCollection services, AccessGrant? grant = null, ClaimsPrincipal? user = null)
    {
        grant ??= Global(AccessRole.Admin);
        user ??= User();
        services.RemoveAll<CurrentAccess>();
        services.RemoveAll<IRbacEvaluator>();
        services.RemoveAll<IAccessGuard>();
        services.AddSingleton(CurrentAccess.CreateLoaded(user, grant));
        services.AddSingleton<IRbacEvaluator>(new FixedRbacEvaluator(grant));
        services.AddSingleton<IAccessGuard>(sp => new AccessGuard(
            sp.GetRequiredService<CurrentAccess>(), sp.GetRequiredService<IRbacEvaluator>(), NullLogger<AccessGuard>.Instance));
        services.TryAddSingleton<AntiforgeryStateProvider, FakeAntiforgeryStateProvider>();
        services.TryAddSingleton(Options.Create(new AuthOptions()));
        return services;
    }

    private sealed class FixedRbacEvaluator(AccessGrant grant) : IRbacEvaluator
    {
        public Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default) => Task.FromResult(grant);
    }

    private sealed class FakeAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-antiforgery-token", "__RequestVerificationToken");
    }
}
