using CodingAgent.Contracts.Auth;
using CodingAgent.Web.Models;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Spec 052 DD4, Req 3.1: what <c>GET /bff/session</c> tells the browser about the signed-in user.
/// The record's shape is the allow-list: the identity claims, the session expiry, the evaluated
/// grant with its matched bindings, and the OIDC provider's display name. <see cref="Build"/> is
/// sent only to a user with some access (n7). The browser builds its principal from these values
/// with <see cref="AuthPrincipals.Create"/>, the same factory the web host uses.
/// </summary>
public sealed record BffSession(
    string Subject,
    string Username,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Groups,
    string IdentitySource,
    DateTimeOffset Expires,
    AccessGrant Grant,
    string OidcProviderName,
    BuildInfo? Build);
