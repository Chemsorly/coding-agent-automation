using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;

namespace CodingAgent.Web.UnitTests.Auth;

public class RbacEvaluatorTests
{
    private const string PaymentsId = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string BillingId = "6f1c2a9e-0000-0000-0000-00000000000b";
    private static readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddHours(1);

    private readonly Mock<IProjectStore> _projects = new();
    private readonly FakeLogger<RbacEvaluator> _logger = new();

    public RbacEvaluatorTests()
    {
        SetProjects(("payments", PaymentsId), ("billing", BillingId));
    }

    private void SetProjects(params (string Name, string Id)[] projects) =>
        _projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects.Select(p => new PipelineProject { Id = p.Id, Name = p.Name }).ToList());

    private RbacEvaluator Evaluator(string defaultRole = "", params RoleBindingOptions[] bindings) =>
        new(Options.Create(new AuthOptions { Rbac = new RbacOptions { DefaultRole = defaultRole, Bindings = bindings.ToList() } }),
            _projects.Object, _logger);

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
            new MatchedBinding("group:team-a", AccessRole.Operator, "payments", PaymentsId, null));
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

    // TODO: UnknownProjectId_GrantsNothing_IsReported_AndLoggedOnce (below) and UnknownProjectId_GrantsNothing_AndReportsUnknownProject
    // (in the Requirement 8 block) use the same binding ID and assert identical outcomes. The only difference is the log
    // de-duplication check in this test. Consider merging them or giving the requirement-8 test a distinct assertion to
    // avoid fully redundant coverage.
    [Fact]
    public async Task UnknownProjectId_GrantsNothing_IsReported_AndLoggedOnce()
    {
        var evaluator = Evaluator("", Group("team-a", "operator", "ffffffff-0000-0000-0000-000000000000"));

        var grant = await evaluator.EvaluateAsync(Oidc("alice", "team-a"));
        await evaluator.EvaluateAsync(Oidc("alice", "team-a"));

        grant.HasAnyAccess.Should().BeFalse();
        grant.Matches.Should().ContainSingle().Which.Problem.Should().Be(RbacEvaluator.UnknownProject);
        _logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Message.Contains("ffffffff-0000-0000-0000-000000000000"));
    }

    [Fact]
    public async Task ProjectListUnavailable_KeepsGlobalBindings_DropsProjectBindings()
    {
        _projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("api down"));
        var evaluator = Evaluator("", Group("team-a", "readonly"), Group("team-a", "operator", PaymentsId));

        var grant = await evaluator.EvaluateAsync(Oidc("alice", "team-a"));

        grant.GlobalRole.Should().Be(AccessRole.ReadOnly);
        grant.RoleFor(PaymentsId).Should().Be(AccessRole.ReadOnly);
        grant.ProjectRoles.Should().BeEmpty();
        grant.Matches.Should().Contain(m => m.Problem == RbacEvaluator.ProjectsUnavailable);
    }

    [Fact]
    public async Task OnlyGlobalBindings_DoNotLoadProjects()
    {
        await Evaluator("", Group("ops", "operator")).EvaluateAsync(Oidc("erin", "ops"));
        _projects.Verify(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StoreProjectId_IsNormalised()
    {
        // The binding uses a canonical lower-case project ID; the store returns the project with
        // an upper-case ID. Resolve() normalizes the store ID before comparing, so it still matches.
        SetProjects(("payments", PaymentsId.ToUpperInvariant()));
        var grant = await Evaluator("", Group("team-a", "operator", PaymentsId)).EvaluateAsync(Oidc("alice", "team-a"));
        grant.ProjectRoles.Keys.Should().ContainSingle().Which.Should().Be(PaymentsId);
    }

    // ── Requirement 8: new tests ─────────────────────────────────────────────

    [Fact]
    public async Task ProjectBinding_AfterRename_StillGrantsRole()
    {
        // Rename the project: same ID, new name.
        SetProjects(("new-name", PaymentsId));
        var grant = await Evaluator("", Group("team-a", "operator", PaymentsId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.Matches.Should().ContainSingle().Which.ProjectName.Should().Be("new-name");
    }

    [Fact]
    public async Task TwoProjectsSameName_BindingToOneId_GrantsOnlyThatProject()
    {
        SetProjects(("payments", PaymentsId), ("payments", BillingId));
        var grant = await Evaluator("", Group("team-a", "operator", PaymentsId)).EvaluateAsync(Oidc("alice", "team-a"));

        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
        grant.RoleFor(BillingId).Should().Be(AccessRole.None);
    }

    [Fact]
    public async Task UnknownProjectId_GrantsNothing_AndReportsUnknownProject()
    {
        var grant = await Evaluator("", Group("team-a", "operator", "ffffffff-0000-0000-0000-000000000000"))
            .EvaluateAsync(Oidc("alice", "team-a"));

        grant.HasAnyAccess.Should().BeFalse();
        grant.Matches.Should().ContainSingle().Which.Problem.Should().Be(RbacEvaluator.UnknownProject);
    }

    [Fact]
    public async Task NormalizedProjectId_Matches()
    {
        // TODO: This test covers the upper-case GUID form (ToUpperInvariant()) but not the brace-wrapped form
        // (e.g. "{6f1c2a9e-0000-0000-0000-000000000000}"). Guid.TryParse accepts the {D} format and ProjectIds.Normalize
        // handles it, but there is no test for that code path. Add a brace-wrapped variant to fully cover Requirement 8.
        // Binding is configured with an upper-case GUID; From() normalizes it at construction.
        // The store returns the project with a lower-case ID. The lookup must match.
        var grant = await Evaluator("", Group("team-a", "operator", PaymentsId.ToUpperInvariant()))
            .EvaluateAsync(Oidc("alice", "team-a"));

        grant.RoleFor(PaymentsId).Should().Be(AccessRole.Operator);
    }
}
