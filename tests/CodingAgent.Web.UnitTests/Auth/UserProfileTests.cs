using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Auth;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 9: the profile page. Spec 051 D2a: project names come from /api/ui/me.</summary>
public class UserProfileTests : BunitContext
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string UnknownId = "aaaaaaaa-0000-0000-0000-000000000000";

    private readonly FakeMeClient _me = new();

    public UserProfileTests()
    {
        Services.AddSingleton<IAuthUiInfo>(new AuthUiInfo(Options.Create(new AuthOptions { Oidc = { Name = "Keycloak" } })));
        Services.AddSingleton<IUiMeClient>(_me);
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
        _me.Calls.Should().Be(0, "a grant without project roles has no project names to resolve");
    }

    [Fact]
    public void OidcUser_ShowsGroupsAndMatchedBindings_WithProjectNamesFromTheApi()
    {
        var grant = new AccessGrant(
            AccessRole.ReadOnly,
            new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.Operator },
            [
                new MatchedBinding("group:readers", AccessRole.ReadOnly, null),
                new MatchedBinding("group:team-a", AccessRole.Operator, PaymentsId),
            ]);
        Services.AddTestAccess(grant, TestAccess.User("alice", "readers", "team-a", "6f1c2a9e-1111-0000-0000-000000000000"));
        _me.ProjectRoles = [new UiMeProjectRole(PaymentsId, "payments", "operator", Known: true)];

        var cut = Render<UserProfile>();

        cut.Find("[data-testid=profile-source]").TextContent.Should().Be("Keycloak");
        cut.Find("[data-testid=profile-global-role]").TextContent.Should().Be("readonly");
        cut.FindAll("[data-testid=profile-groups] li").Select(li => li.TextContent).Should()
            .Equal("readers", "team-a", "6f1c2a9e-1111-0000-0000-000000000000");
        var rows = cut.FindAll("[data-testid=profile-bindings] tbody tr").Select(r => r.TextContent).ToList();
        rows.Should().HaveCount(2);
        rows[0].Should().Contain("Global");
        rows[1].Should().Contain("Project payments").And.Contain(PaymentsId).And.Contain("operator").And.Contain("group:team-a");
        cut.FindAll("[data-testid=profile-binding-warning]").Should().BeEmpty();
    }

    [Fact]
    public void BindingToAnIdNoProjectHas_IsShownAsWarning_NotAsARow()
    {
        var grant = new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole> { [UnknownId] = AccessRole.Operator },
        [
            new MatchedBinding("group:team-a", AccessRole.Operator, UnknownId),
        ]);
        Services.AddTestAccess(grant, TestAccess.User("alice", "team-a"));
        _me.ProjectRoles = [new UiMeProjectRole(UnknownId, null, "operator", Known: false)];

        var cut = Render<UserProfile>();

        var warnings = cut.FindAll("[data-testid=profile-binding-warning]").Select(w => w.TextContent).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain(UnknownId).And.Contain("group:team-a").And.Contain("no project has this ID");
        cut.FindAll("[data-testid=profile-bindings] tbody tr").Should().BeEmpty();
    }

    [Fact]
    public void ApiUnavailable_ShowsProjectIdsOnly_AndSaysSo()
    {
        var grant = new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole> { [PaymentsId] = AccessRole.ReadOnly },
        [
            new MatchedBinding("group:team-a", AccessRole.ReadOnly, PaymentsId),
        ]);
        Services.AddTestAccess(grant, TestAccess.User("alice", "team-a"));
        _me.Failure = new HttpRequestException("api down");

        var cut = Render<UserProfile>();

        cut.FindAll("[data-testid=profile-projects-unavailable]").Should().ContainSingle();
        cut.Find("[data-testid=profile-bindings] tbody tr").TextContent.Should().Contain($"Project {PaymentsId}");
        cut.FindAll("[data-testid=profile-binding-warning]").Should().BeEmpty();
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
        _me.Calls.Should().Be(0);
    }

    private sealed class FakeMeClient : IUiMeClient
    {
        public IReadOnlyList<UiMeProjectRole> ProjectRoles { get; set; } = [];
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public Task<UiMeResponse> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            return Failure is not null
                ? Task.FromException<UiMeResponse>(Failure)
                : Task.FromResult(new UiMeResponse("sub-alice", "alice", "oidc", "none", ProjectRoles));
        }
    }
}
