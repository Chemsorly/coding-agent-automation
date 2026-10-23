using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Auth;

/// <summary>Spec 051: a grant written into token claims reads back unchanged; malformed claims grant nothing.</summary>
public class AccessGrantClaimsTests
{
    private const string ProjectA = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string ProjectB = "6f1c2a9e-0000-0000-0000-00000000000b";

    public static TheoryData<string, AccessGrant> Grants => new()
    {
        { "empty", AccessGrant.None },
        { "global only", new AccessGrant(AccessRole.Operator, new Dictionary<string, AccessRole>(), []) },
        { "scoped only", new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole> { [ProjectA] = AccessRole.ReadOnly }, []) },
        { "project admin", new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole> { [ProjectA] = AccessRole.Admin }, []) },
        {
            "mixed",
            new AccessGrant(AccessRole.ReadOnly,
                new Dictionary<string, AccessRole> { [ProjectA] = AccessRole.Operator, [ProjectB] = AccessRole.ReadOnly }, [])
        },
    };

    [Theory]
    [MemberData(nameof(Grants), DisableDiscoveryEnumeration = true)]
    public void RoundTrip_KeepsGlobalAndProjectRoles(string name, AccessGrant grant)
    {
        var readBack = AccessGrantClaims.FromClaims(AccessGrantClaims.ToClaims(grant));

        readBack.GlobalRole.Should().Be(grant.GlobalRole, name);
        readBack.ProjectRoles.Should().BeEquivalentTo(grant.ProjectRoles, name);
        readBack.Matches.Should().BeEmpty("the bindings that produced a grant are not carried");
    }

    [Fact]
    public void ToClaims_RejectsNull_BeforeEnumeration()
    {
        var act = () => AccessGrantClaims.ToClaims(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AProjectScopedAdmin_CoversThatProjectOnly()
    {
        // #3577: the API checks a project admin on the project of the object it acts on.
        var grant = AccessGrantClaims.FromClaims([new Claim(UserTokenClaimTypes.ProjectRole, $"{ProjectA}:admin")]);

        grant.RoleFor(ProjectA).Should().Be(AccessRole.Admin);
        grant.RoleFor(ProjectB).Should().Be(AccessRole.None);
        grant.GlobalRole.Should().Be(AccessRole.None);
    }

    [Theory]
    [InlineData("no-separator")]
    [InlineData(":operator")]
    [InlineData("6f1c2a9e-0000-0000-0000-00000000000a:")]
    [InlineData("6f1c2a9e-0000-0000-0000-00000000000a:superuser")]
    public void FromClaims_IgnoresMalformedProjectRoles(string value)
    {
        AccessGrantClaims.FromClaims([new Claim(UserTokenClaimTypes.ProjectRole, value)]).HasAnyAccess.Should().BeFalse();
    }

    [Fact]
    public void FromClaims_KeepsTheHigherRole_AndNormalisesProjectIds()
    {
        var grant = AccessGrantClaims.FromClaims(
        [
            new Claim(UserTokenClaimTypes.GlobalRole, "readonly"),
            new Claim(UserTokenClaimTypes.GlobalRole, "operator"),
            new Claim(UserTokenClaimTypes.ProjectRole, $"{ProjectA.ToUpperInvariant()}:readonly"),
            new Claim(UserTokenClaimTypes.ProjectRole, $"{ProjectA}:operator"),
        ]);

        grant.GlobalRole.Should().Be(AccessRole.Operator);
        grant.ProjectRoles.Should().Equal(new Dictionary<string, AccessRole> { [ProjectA] = AccessRole.Operator });
    }

    [Fact]
    public void FromClaims_IgnoresUnknownGlobalRoleNames()
    {
        AccessGrantClaims.FromClaims([new Claim(UserTokenClaimTypes.GlobalRole, "root")]).GlobalRole.Should().Be(AccessRole.None);
    }
}
