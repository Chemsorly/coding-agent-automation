using AwesomeAssertions;
using CodingAgent.Web.Auth;

namespace CodingAgent.Web.UnitTests.Auth;

public class AccessGrantTests
{
    private const string ProjectP = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string ProjectQ = "6f1c2a9e-0000-0000-0000-00000000000b";

    private static AccessGrant Grant(AccessRole global, params (string Id, AccessRole Role)[] projects) =>
        new(global, projects.ToDictionary(p => p.Id, p => p.Role), []);

    [Fact]
    public void RoleFor_ProjectRoleHigherThanGlobal_ReturnsProjectRole()
    {
        Grant(AccessRole.ReadOnly, (ProjectP, AccessRole.Operator)).RoleFor(ProjectP).Should().Be(AccessRole.Operator);
    }

    [Fact]
    public void RoleFor_GlobalRoleHigherThanProject_ReturnsGlobalRole()
    {
        Grant(AccessRole.Operator, (ProjectP, AccessRole.ReadOnly)).RoleFor(ProjectP).Should().Be(AccessRole.Operator);
    }

    [Fact]
    public void RoleFor_OtherProject_ReturnsGlobalRole()
    {
        Grant(AccessRole.None, (ProjectP, AccessRole.Operator)).RoleFor(ProjectQ).Should().Be(AccessRole.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RoleFor_NoProject_ReturnsGlobalRoleOnly(string? projectId)
    {
        Grant(AccessRole.None, (ProjectP, AccessRole.Operator)).RoleFor(projectId).Should().Be(AccessRole.None);
        Grant(AccessRole.ReadOnly, (ProjectP, AccessRole.Operator)).RoleFor(projectId).Should().Be(AccessRole.ReadOnly);
    }

    [Theory]
    [InlineData("6F1C2A9E-0000-0000-0000-00000000000A")]
    [InlineData("{6f1c2a9e-0000-0000-0000-00000000000a}")]
    [InlineData("6f1c2a9e00000000000000000000000a")]
    public void RoleFor_GuidInOtherFormat_MatchesNormalisedId(string projectId)
    {
        Grant(AccessRole.None, (ProjectP, AccessRole.Operator)).RoleFor(projectId).Should().Be(AccessRole.Operator);
    }

    [Fact]
    public void Flags_ScopedUser()
    {
        var grant = Grant(AccessRole.None, (ProjectP, AccessRole.ReadOnly));
        grant.IsScoped.Should().BeTrue();
        grant.HasAnyAccess.Should().BeTrue();
        grant.HasAnyOperator.Should().BeFalse();
    }

    [Fact]
    public void Flags_ScopedOperator()
    {
        var grant = Grant(AccessRole.None, (ProjectP, AccessRole.ReadOnly), (ProjectQ, AccessRole.Operator));
        grant.HasAnyOperator.Should().BeTrue();
    }

    [Fact]
    public void Flags_GlobalUser()
    {
        var grant = Grant(AccessRole.Operator);
        grant.IsScoped.Should().BeFalse();
        grant.HasAnyAccess.Should().BeTrue();
        grant.HasAnyOperator.Should().BeTrue();
    }

    [Fact]
    public void Flags_NoAccess()
    {
        AccessGrant.None.IsScoped.Should().BeFalse();
        AccessGrant.None.HasAnyAccess.Should().BeFalse();
        AccessGrant.None.HasAnyOperator.Should().BeFalse();
    }

    [Fact]
    public void RoleFor_DefaultProjectWithAllZeroId_MatchesGuidAndStringForms()
    {
        var grant = Grant(AccessRole.None, ("00000000-0000-0000-0000-000000000000", AccessRole.Operator));

        grant.RoleFor(ProjectIds.Normalize(Guid.Empty)).Should().Be(AccessRole.Operator);
        grant.RoleFor("00000000-0000-0000-0000-000000000000").Should().Be(AccessRole.Operator);
    }

    [Fact]
    public void ProjectIds_NormalizeGuid()
    {
        ProjectIds.Normalize(Guid.Parse(ProjectP)).Should().Be(ProjectP);
        ProjectIds.Normalize((Guid?)null).Should().BeNull();
        ProjectIds.Normalize(Guid.Empty).Should().Be("00000000-0000-0000-0000-000000000000",
            "the default project's ID is the all-zero GUID");
        ProjectIds.Normalize(" legacy-id ").Should().Be("legacy-id");
    }
}
