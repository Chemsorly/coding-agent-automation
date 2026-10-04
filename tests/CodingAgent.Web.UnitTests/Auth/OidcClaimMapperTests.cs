using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Web.Auth;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 3.4–3.6: ID-token claims to session identity, for Keycloak- and Entra-shaped tokens.</summary>
public class OidcClaimMapperTests
{
    private static readonly DateTimeOffset Expires = new(2026, 10, 4, 22, 0, 0, TimeSpan.Zero);
    private static readonly OidcProviderOptions Options = new();

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    [Fact]
    public void KeycloakToken_KeepsOnlySessionClaims()
    {
        var token = Token(
            ("sub", "f81d4fae"), ("preferred_username", "alice"), ("email", "alice@example.test"),
            ("name", "Alice Doe"), ("groups", "team-a"), ("groups", "platform-team"),
            ("aud", "coding-agent"), ("nonce", "n-0S6_WzA2Mj"), ("azp", "coding-agent"), ("acr", "1"));

        var result = OidcClaimMapper.Map(token, Options, Expires);

        var principal = result.Principal;
        principal.Claims.Select(c => c.Type).Distinct().Should().BeEquivalentTo(
            [AuthClaimTypes.Subject, AuthClaimTypes.Username, AuthClaimTypes.Email, AuthClaimTypes.DisplayName,
             AuthClaimTypes.Group, AuthClaimTypes.IdentitySource, AuthClaimTypes.SessionExpires]);
        principal.FindFirst(AuthClaimTypes.Subject)!.Value.Should().Be("f81d4fae");
        principal.Identity!.Name.Should().Be("alice");
        principal.FindFirst(AuthClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Oidc);
        AuthPrincipals.GetGroups(principal).Should().Equal("team-a", "platform-team");
        AuthPrincipals.GetSessionExpiry(principal).Should().Be(Expires);
        AuthPrincipals.IsLocalAdmin(principal).Should().BeFalse();
        result.Subject.Should().Be("f81d4fae");
        result.GroupCount.Should().Be(2);
        result.GroupOverage.Should().BeFalse();
    }

    [Fact]
    public void KeycloakFullGroupPath_IsKeptVerbatim()
    {
        var result = OidcClaimMapper.Map(Token(("sub", "s"), ("groups", "/org/team-a")), Options, Expires);
        AuthPrincipals.GetGroups(result.Principal).Should().Equal("/org/team-a");
    }

    [Fact]
    public void GroupsAsSerializedJsonArray_AreSplit()
    {
        var result = OidcClaimMapper.Map(Token(("sub", "s"), ("groups", "[\"a\",\"b\",\"a\"]")), Options, Expires);
        AuthPrincipals.GetGroups(result.Principal).Should().Equal("a", "b");
    }

    [Fact]
    public void MissingGroupsClaim_MeansNoGroups()
    {
        var result = OidcClaimMapper.Map(Token(("sub", "s"), ("preferred_username", "carol")), Options, Expires);
        AuthPrincipals.GetGroups(result.Principal).Should().BeEmpty();
        result.GroupOverage.Should().BeFalse();
    }

    [Fact]
    public void EntraToken_GroupObjectIds_AndRolesClaimWhenConfigured()
    {
        var token = Token(
            ("sub", "AAAAAAAAAAAAAAAAAAAAAIkzqFVrSaSaFHy782bbtaQ"), ("oid", "00000000-0000-0000-66f3-3332eca7ea81"),
            ("preferred_username", "alice@contoso.com"), ("name", "Alice"),
            ("groups", "6f1c2a9e-0000-0000-0000-00000000000a"), ("roles", "CodingAgent.Operators"));

        var byGroups = OidcClaimMapper.Map(token, Options, Expires);
        var byRoles = OidcClaimMapper.Map(token, new OidcProviderOptions { GroupsClaim = "roles" }, Expires);

        byGroups.Principal.Identity!.Name.Should().Be("alice@contoso.com");
        AuthPrincipals.GetGroups(byGroups.Principal).Should().Equal("6f1c2a9e-0000-0000-0000-00000000000a");
        AuthPrincipals.GetGroups(byRoles.Principal).Should().Equal("CodingAgent.Operators");
    }

    [Fact]
    public void EntraOverage_ClaimNames_IsDetected()
    {
        var token = Token(("sub", "s"), ("_claim_names", "{\"groups\":\"src1\"}"),
            ("_claim_sources", "{\"src1\":{\"endpoint\":\"https://graph.microsoft.com/v1.0/users/x/getMemberObjects\"}}"));

        var result = OidcClaimMapper.Map(token, Options, Expires);

        result.GroupOverage.Should().BeTrue();
        AuthPrincipals.GetGroups(result.Principal).Should().BeEmpty();
    }

    [Fact]
    public void EntraOverage_HasGroups_IsDetected()
    {
        OidcClaimMapper.Map(Token(("sub", "s"), ("hasgroups", "true")), Options, Expires).GroupOverage.Should().BeTrue();
    }

    [Fact]
    public void ClaimNamesForOtherClaims_IsNotOverage()
    {
        OidcClaimMapper.Map(Token(("sub", "s"), ("_claim_names", "{\"roles\":\"src1\"}")), Options, Expires)
            .GroupOverage.Should().BeFalse();
    }

    [Theory]
    [InlineData("preferred_username", "alice", "alice")]
    [InlineData("upn", "alice@corp.example", "alice@corp.example")]
    public void Username_ComesFromConfiguredClaim(string usernameClaim, string value, string expected)
    {
        var token = Token(("sub", "s"), (usernameClaim, value), ("email", "other@example.test"));
        OidcClaimMapper.Map(token, new OidcProviderOptions { UsernameClaim = usernameClaim }, Expires)
            .Username.Should().Be(expected);
    }

    [Fact]
    public void Username_FallsBackToEmail_ThenSubject()
    {
        OidcClaimMapper.Map(Token(("sub", "s-1"), ("email", "bob@example.test")), Options, Expires)
            .Username.Should().Be("bob@example.test");
        OidcClaimMapper.Map(Token(("sub", "s-1"), ("preferred_username", " ")), Options, Expires)
            .Username.Should().Be("s-1");
    }
}
