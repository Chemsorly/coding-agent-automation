using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 9: the profile page.</summary>
public class UserProfileTests : BunitContext
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";

    public UserProfileTests()
    {
        Services.AddSingleton(Options.Create(new AuthOptions { Oidc = { Name = "Keycloak" } }));
    }

    [Fact]
    public void LocalAdmin_ShowsSourceAndGlobalAdmin()
    {
        Services.AddTestAccess(TestAccess.Global(AccessRole.Admin), AuthPrincipals.CreateLocalAdmin(TestAccess.Expires));

        var cut = Render<UserProfile>();

        cut.Find("[data-testid=profile-username]").TextContent.Should().Be("admin");
        cut.Find("[data-testid=profile-source]").TextContent.Should().Be("Local admin");
        cut.Find("[data-testid=profile-global-role]").TextContent.Should().Be("admin");
        cut.Markup.Should().Contain("Bindings do not apply");
    }

    [Fact]
    public void OidcUser_ShowsGroupsAndMatchedBindings()
    {
        var grant = new AccessGrant(
            AccessRole.ReadOnly,
            new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.Operator },
            [
                new MatchedBinding("group:readers", AccessRole.ReadOnly, null, null, null),
                new MatchedBinding("group:team-a", AccessRole.Operator, "payments", PaymentsId, null),
            ]);
        Services.AddTestAccess(grant, TestAccess.User("alice", "readers", "team-a", "6f1c2a9e-1111-0000-0000-000000000000"));

        var cut = Render<UserProfile>();

        cut.Find("[data-testid=profile-source]").TextContent.Should().Be("Keycloak");
        cut.Find("[data-testid=profile-global-role]").TextContent.Should().Be("readonly");
        cut.FindAll("[data-testid=profile-groups] li").Select(li => li.TextContent).Should()
            .Equal("readers", "team-a", "6f1c2a9e-1111-0000-0000-000000000000");
        var rows = cut.FindAll("[data-testid=profile-bindings] tbody tr").Select(r => r.TextContent).ToList();
        rows.Should().HaveCount(2);
        rows[1].Should().Contain("Project payments").And.Contain(PaymentsId).And.Contain("operator").And.Contain("group:team-a");
    }

    [Fact]
    public void BindingWithProblem_IsShownAsWarning()
    {
        var grant = new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole>(),
        [
            new MatchedBinding("group:team-a", AccessRole.Operator, null, "aaaaaaaa-0000-0000-0000-000000000000", RbacEvaluator.UnknownProject),
        ]);
        Services.AddTestAccess(grant, TestAccess.User("alice", "team-a"));

        var cut = Render<UserProfile>();

        var warnings = cut.FindAll("[data-testid=profile-binding-warning]").Select(w => w.TextContent).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("aaaaaaaa-0000-0000-0000-000000000000").And.Contain("unknown project");
    }

    [Fact]
    public void NoAccessUser_SeesProfile_WithNoBindingsAndNoGroups()
    {
        Services.AddTestAccess(AccessGrant.None, TestAccess.User("carol"));

        var cut = Render<UserProfile>();

        cut.Find("[data-testid=profile-username]").TextContent.Should().Be("carol");
        cut.FindAll("[data-testid=profile-no-groups]").Should().ContainSingle();
        cut.FindAll("[data-testid=profile-no-bindings]").Should().ContainSingle();
        cut.Markup.Should().NotContain("Back to overview");
        cut.FindAll("[data-testid=profile-logout]").Should().ContainSingle();
    }
}
