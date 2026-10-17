using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.Auth;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 051 T8, spec 052 Req 14.2: minting user tokens in the web host.</summary>
public class UserTokenMinterTests
{
    internal static readonly SecurityKey Key = UserTokens.CreateSigningKey("unit-test-user-token-signing-key-0123456789");
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeLogger<UserTokenMinter> _logger = new();
    private AccessGrant _grant = new(AccessRole.ReadOnly, new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.Operator }, []);

    private UserTokenMinter Minter() => new(new MappedRbacEvaluator(_ => _grant), new UserTokenSigningKey(Key), _time, _logger);

    private ClaimsPrincipal User(string username = "alice", TimeSpan? session = null) =>
        AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, email: null, displayName: null,
            groups: ["team-a"], _time.GetUtcNow() + (session ?? TimeSpan.FromHours(8)));

    internal static async Task<ClaimsIdentity> ValidateAsync(string token)
    {
        var parameters = UserTokens.CreateValidationParameters(Key);
        parameters.LifetimeValidator = (_, _, _, _) => true; // the fake clock is checked through the exp claim instead
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        result.IsValid.Should().BeTrue(result.Exception?.Message);
        return result.ClaimsIdentity;
    }

    internal static TimeSpan Lifetime(string token)
    {
        var jwt = new JsonWebToken(token);
        return jwt.ValidTo - jwt.IssuedAt;
    }

    [Fact]
    public async Task Token_CarriesTheUserAndExactlyTheirGrant()
    {
        var token = await Minter().MintAsync(User(), hub: false);

        var identity = await ValidateAsync(token!.Value);
        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
        identity.FindFirst(UserTokenClaimTypes.Username)!.Value.Should().Be("alice");
        identity.FindFirst(UserTokenClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Oidc);
        var readBack = AccessGrantClaims.FromClaims(identity.Claims);
        readBack.GlobalRole.Should().Be(_grant.GlobalRole);
        readBack.ProjectRoles.Should().BeEquivalentTo(_grant.ProjectRoles);
        identity.FindAll(AuthClaimTypes.Group).Should().BeEmpty("the token carries the grant, not the IdP's groups");
    }

    [Fact]
    public async Task Lifetime_IsTheHttpLifetime_WhenTheSessionLastsLonger()
    {
        var token = await Minter().MintAsync(User(), hub: false);

        Lifetime(token!.Value).Should().Be(UserTokens.HttpLifetime);
        token.Expires.Should().Be(_time.GetUtcNow() + UserTokens.HttpLifetime);
    }

    [Fact]
    public async Task Lifetime_EndsWithTheSession()
    {
        var token = await Minter().MintAsync(User(session: TimeSpan.FromMinutes(2)), hub: false);

        Lifetime(token!.Value).Should().BeCloseTo(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Spec 051 D7: the UI hub's token lasts the session, so a connection older than the HTTP lifetime keeps
    /// receiving; the hub closes it when the session ends. It is the same user and grant as the HTTP token.
    /// </summary>
    [Fact]
    public async Task HubToken_LastsTheSession_AndIsMarkedForTheHubOnly()
    {
        var user = User(session: TimeSpan.FromHours(8));

        var hubToken = await Minter().MintAsync(user, hub: true);
        var httpToken = await Minter().MintAsync(user, hub: false);

        Lifetime(hubToken!.Value).Should().BeCloseTo(TimeSpan.FromHours(8), TimeSpan.FromSeconds(1));
        Lifetime(httpToken!.Value).Should().Be(UserTokens.HttpLifetime);
        (await ValidateAsync(hubToken.Value)).FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be("sub-alice");
        (await ValidateAsync(hubToken.Value)).FindFirst(UserTokenClaimTypes.Use)?.Value.Should().Be(UserTokenClaimTypes.HubUse, "the API accepts it only on the hub");
        (await ValidateAsync(httpToken.Value)).FindFirst(UserTokenClaimTypes.Use).Should().BeNull("HTTP tokens carry no use restriction");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndedSession_GetsNoToken(bool hub)
    {
        var user = User(session: TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        (await Minter().MintAsync(user, hub)).Should().BeNull();
    }

    [Fact]
    public async Task Anonymous_GetsNoToken()
    {
        (await Minter().MintAsync(new ClaimsPrincipal(new ClaimsIdentity()), hub: false)).Should().BeNull();
    }

    [Fact]
    public async Task UserWithoutAnyRole_GetsNoToken()
    {
        _grant = AccessGrant.None;

        (await Minter().MintAsync(User(), hub: false)).Should().BeNull();
    }

    [Fact]
    public async Task LocalAdmin_GetsAnAdminToken()
    {
        _grant = new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []);

        var token = await Minter().MintAsync(AuthPrincipals.CreateLocalAdmin(_time.GetUtcNow().AddHours(1)), hub: false);

        var identity = await ValidateAsync(token!.Value);
        identity.FindFirst(UserTokenClaimTypes.Subject)!.Value.Should().Be(AuthPrincipals.LocalAdminSubject);
        identity.FindFirst(UserTokenClaimTypes.IdentitySource)!.Value.Should().Be(IdentitySources.Local);
        AccessGrantClaims.FromClaims(identity.Claims).GlobalRole.Should().Be(AccessRole.Admin);
    }

    [Fact]
    public async Task TokenValue_IsNeverLogged()
    {
        var token = await Minter().MintAsync(User(), hub: false);

        var records = _logger.Collector.GetSnapshot();
        records.Should().NotBeEmpty("minting is logged, so the assertion below has something to check");
        foreach (var record in records)
        {
            record.Message.Should().NotContain(token!.Value);
            (record.StructuredState ?? []).Should().NotContain(pair => pair.Value != null && pair.Value.Contains(token.Value));
        }
    }
}

internal sealed class MappedRbacEvaluator(Func<ClaimsPrincipal, AccessGrant> map) : IRbacEvaluator
{
    public Task<AccessGrant> EvaluateAsync(ClaimsPrincipal user, CancellationToken ct = default) => Task.FromResult(map(user));
}
