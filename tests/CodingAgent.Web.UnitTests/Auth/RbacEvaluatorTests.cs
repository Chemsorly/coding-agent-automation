using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Auth;

public class RbacEvaluatorTests
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string BillingId = "6f1c2a9e-0000-0000-0000-00000000000b";
    private static readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddHours(1);

    private static RbacEvaluator Evaluator(string defaultRole = "", params RoleBindingOptions[] bindings) =>
        new(Options.Create(new AuthOptions { Rbac = new RbacOptions { DefaultRole = defaultRole, Bindings = bindings.ToList() } }));

    private static ClaimsPrincipal Oidc(string username, params string[] groups) =>
        AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{username}", username, email: null, displayName: null, groups, Expires);

    private static RoleBindingOptions Group(string group, string role, string? projectId = null) =>
        new() { Group = group, Role = role, ProjectId = projectId };

    private static RoleBindingOptions User(string user, string role, string? projectId = null) =>
        new() { User = user, Role = role, ProjectId = projectId };

    [Fact]
    public async Task Unauthenticated_GetsNothing()
    {
        var grant = await Evaluator("operator").EvaluateAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        grant.Should().Be(AccessGrant.None);
    }

    [Fact]
    public async Task LocalAdmin_IsGlobalAdmin_AndIgnoresBindings()
    {
        var grant = await Evaluator("", User("admin", "readonly")).EvaluateAsync(AuthPrincipals.CreateLocalAdmin(Expires));
        grant.GlobalRole.Should().Be(AccessRole.Admin);
        grant.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task OidcUserNamedAdmin_IsNotLocalAdmin()
    {
        var grant = await Evaluator().EvaluateAsync(Oidc("admin"));
        grant.GlobalRole.Should().Be(AccessRole.None);
        grant.HasAnyAccess.Should().BeFalse();
    }

    [Fact]
    public async Task NoMatchingBinding_GetsDefaultRole()
    {
        (await Evaluator().EvaluateAsync(Oidc("carol"))).HasAnyAccess.Should().BeFalse();
        (await Evaluator("readonly").EvaluateAsync(Oidc("carol"))).GlobalRole.Should().Be(AccessRole.ReadOnly);
        (await Evaluator("operator").EvaluateAsync(Oidc("carol"))).GlobalRole.Should().Be(AccessRole.Operator);
    }

    [Fact]
    public async Task GroupBinding_MatchesExactCase()
    {
        var evaluator = Evaluator("", Group("Platform-Team", "admin"));
        (await evaluator.EvaluateAsync(Oidc("bob", "Platform-Team"))).GlobalRole.Should().Be(AccessRole.Admin);
        (await evaluator.EvaluateAsync(Oidc("bob", "platform-team"))).GlobalRole.Should().Be(AccessRole.None);
    }

    [Fact]
    public async Task UserBinding_MatchesCaseInsensitive()
    {
        var evaluator = Evaluator("", User("Alice@Acme.com", "readonly"));
        var grant = await evaluator.EvaluateAsync(Oidc("alice@acme.com"));
        grant.GlobalRole.Should().Be(AccessRole.ReadOnly);
        grant.Matches.Should().ContainSingle().Which.Subject.Should().Be("user:Alice@Acme.com");
    }

    [Fact]
    public async Task HighestGlobalRoleWins()
    {
        var evaluator = Evaluator("readonly", Group("a", "operator"), Group("b", "admin"), User("dave", "readonly"));
        var grant = await evaluator.EvaluateAsync(Oidc("dave", "a", "b"));
        grant.GlobalRole.Should().Be(AccessRole.Admin);
        grant.Matches.Should().HaveCount(3);
    }

    [Fact]
    public async Task ProjectBinding_GrantsRoleOnThatProjectOnly()
    {
        var grant = await Evaluator("", Group("team-a", "operator", PaymentsId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.GlobalRole.Should().Be(AccessRole.None);
        grant.IsScoped.Should().BeTrue();
        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.RoleFor(BillingId).Should().Be(AccessRole.None);
        grant.Matches.Should().ContainSingle().Which.Should().Be(
            new MatchedBinding("group:team-a", AccessRole.Operator, PaymentsId));
    }

    [Fact]
    public async Task SeveralProjectBindings_TakeHighestPerProject()
    {
        var evaluator = Evaluator("",
            Group("team-a", "readonly", PaymentsId),
            User("alice", "operator", PaymentsId),
            Group("team-a", "readonly", BillingId));
        var grant = await evaluator.EvaluateAsync(Oidc("alice", "team-a"));

        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.RoleFor(BillingId).Should().Be(AccessRole.ReadOnly);
    }

    [Fact]
    public async Task ProjectBinding_NeverLowersGlobalRole()
    {
        var evaluator = Evaluator("", Group("ops", "operator"), Group("ops", "readonly", PaymentsId));
        var grant = await evaluator.EvaluateAsync(Oidc("erin", "ops"));

        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.IsScoped.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectIdNoProjectCarries_GrantsTheRoleOnThatIdAndNothingElse()
    {
        // Spec 051 D2a: bindings are not checked against the project list. The role lands on an
        // ID that no object carries, so it opens nothing; the profile page flags the binding.
        const string unknownId = "ffffffff-0000-0000-0000-000000000000";
        var grant = await Evaluator("", Group("team-a", "operator", unknownId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.ProjectRoles.Should().Equal(new Dictionary<string, AccessRole> { [unknownId] = AccessRole.Operator });
        grant.GlobalRole.Should().Be(AccessRole.None);
        grant.RoleFor(PaymentsId).Should().Be(AccessRole.None);
        grant.Matches.Should().ContainSingle().Which.ProjectId.Should().Be(unknownId);
    }

    [Theory]
    [InlineData("6F1C2A9E-0000-0000-0000-00000000000A")]
    [InlineData("{6f1c2a9e-0000-0000-0000-00000000000a}")]
    [InlineData("  6f1c2a9e-0000-0000-0000-00000000000a ")]
    public async Task BindingProjectId_IsNormalised(string configured)
    {
        var grant = await Evaluator("", Group("team-a", "operator", configured)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.ProjectRoles.Keys.Should().ContainSingle().Which.Should().Be(PaymentsId);
        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.Matches.Should().ContainSingle().Which.ProjectId.Should().Be(PaymentsId);
    }

    [Fact]
    public async Task ProjectScopedAdminBinding_GrantsAdminOnThatProject()
    {
        // Admin may now be bound to a non-default project.
        var grant = await Evaluator("", Group("team-a", "admin", PaymentsId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.HasAnyAccess.Should().BeTrue();
        grant.GlobalRole.Should().Be(AccessRole.None, "a project admin has no global admin role");
        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Admin);
        grant.RoleFor(BillingId).Should().Be(AccessRole.None);
        grant.Matches.Should().ContainSingle().Which.Should().Be(
            new MatchedBinding("group:team-a", AccessRole.Admin, PaymentsId));
    }

    [Fact]
    public async Task DefaultProjectBinding_IsIgnored()
    {
        // The all-zeros Default project takes no project bindings; Binding.From returns null.
        const string defaultProjectId = "00000000-0000-0000-0000-000000000000";
        var grant = await Evaluator("", Group("team-a", "operator", defaultProjectId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.HasAnyAccess.Should().BeFalse();
        grant.Matches.Should().BeEmpty();
    }
}
