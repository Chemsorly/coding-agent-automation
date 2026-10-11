using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Infrastructure.Common.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Auth;

/// <summary>Spec 051 D2: the user token the web host signs and the Pipeline API validates.</summary>
public class UserTokensTests
{
    private const string Key = "unit-test-user-token-signing-key-0123456789";
    private const string ProjectA = "6f1c2a9e-0000-0000-0000-00000000000a";

    private static readonly UserTokenSubject Alice = new("oidc:alice", "alice", "oidc");

    private static AccessGrant Grant(AccessRole global, params (string Project, AccessRole Role)[] projects) =>
        new(global, projects.ToDictionary(p => p.Project, p => p.Role), []);

    private static Task<TokenValidationResult> ValidateAsync(string token, string key = Key) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, UserTokens.CreateValidationParameters(UserTokens.CreateSigningKey(key)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-0123456789")]
    public void CreateSigningKey_RejectsMissingOrShortKeys(string? configured)
    {
        var act = () => UserTokens.CreateSigningKey(configured);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{UserTokens.SigningKeyConfigKey}*");
    }

    [Fact]
    public async Task Create_ThenValidate_RoundTripsSubjectAndGrant()
    {
        var now = DateTimeOffset.UtcNow;
        var grant = Grant(AccessRole.ReadOnly, (ProjectA, AccessRole.Operator));

        var token = UserTokens.Create(Alice, grant, now, now + UserTokens.HttpLifetime, UserTokens.CreateSigningKey(Key));
        var result = await ValidateAsync(token);

        result.IsValid.Should().BeTrue(result.Exception?.Message);
        var claims = result.ClaimsIdentity.Claims.ToList();
        claims.Should().Contain(c => c.Type == UserTokenClaimTypes.Subject && c.Value == "oidc:alice");
        claims.Should().Contain(c => c.Type == UserTokenClaimTypes.Username && c.Value == "alice");
        claims.Should().Contain(c => c.Type == UserTokenClaimTypes.IdentitySource && c.Value == "oidc");
        var readBack = AccessGrantClaims.FromClaims(claims);
        readBack.GlobalRole.Should().Be(AccessRole.ReadOnly);
        readBack.ProjectRoles.Should().Equal(new Dictionary<string, AccessRole> { [ProjectA] = AccessRole.Operator });
    }

    [Fact]
    public void Create_RejectsAnExpiryBeforeTheIssueTime()
    {
        var now = DateTimeOffset.UtcNow;

        var act = () => UserTokens.Create(Alice, AccessGrant.None, now, now, UserTokens.CreateSigningKey(Key));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Validate_RejectsAnotherKey()
    {
        var now = DateTimeOffset.UtcNow;
        var token = UserTokens.Create(Alice, AccessGrant.None, now, now.AddMinutes(5),
            UserTokens.CreateSigningKey("a-different-signing-key-0123456789abcdef"));

        (await ValidateAsync(token)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RejectsAnExpiredToken_BeyondTheClockSkew()
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-10);
        var token = UserTokens.Create(Alice, AccessGrant.None, issued, issued.AddMinutes(5), UserTokens.CreateSigningKey(Key));

        (await ValidateAsync(token)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RejectsAnUnsignedToken()
    {
        var now = DateTime.UtcNow;
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = UserTokens.Issuer,
            Audience = UserTokens.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim(UserTokenClaimTypes.GlobalRole, "admin")]),
        });

        (await ValidateAsync(unsigned)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("someone-else", UserTokens.Audience)]
    [InlineData(UserTokens.Issuer, "someone-else")]
    public async Task Validate_RejectsAnotherIssuerOrAudience(string issuer, string audience)
    {
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim(UserTokenClaimTypes.GlobalRole, "admin")]),
            SigningCredentials = new SigningCredentials(UserTokens.CreateSigningKey(Key), SecurityAlgorithms.HmacSha256),
        });

        (await ValidateAsync(token)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RejectsAnAsymmetricAlgorithm_EvenWithAKnownKeyId()
    {
        // Only HS256 is accepted: a token signed with another algorithm never validates.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = UserTokens.Issuer,
            Audience = UserTokens.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Subject = new ClaimsIdentity([new Claim(UserTokenClaimTypes.GlobalRole, "admin")]),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256),
        });

        (await ValidateAsync(token)).IsValid.Should().BeFalse();
    }
}
