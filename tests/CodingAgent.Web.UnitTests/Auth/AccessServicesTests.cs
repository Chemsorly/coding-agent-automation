using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049: <see cref="CurrentAccess"/>, <see cref="AccessGuard"/> and session revalidation.</summary>
public class AccessServicesTests
{
    private const string ProjectP = "6f1c2a9e-0000-0000-0000-00000000000a";
    private const string ProjectQ = "6f1c2a9e-0000-0000-0000-00000000000b";

    private sealed class SettableAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = user;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));

        public void SetUser(ClaimsPrincipal user)
        {
            _user = user;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }

    private static Mock<IRbacEvaluator> Evaluator(Func<ClaimsPrincipal, AccessGrant> map)
    {
        var rbac = new Mock<IRbacEvaluator>();
        rbac.Setup(r => r.EvaluateAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClaimsPrincipal p, CancellationToken _) => map(p));
        return rbac;
    }

    [Fact]
    public async Task CurrentAccess_IsNotReadyUntilInitialized_ThenAnswersFromTheGrant()
    {
        var user = TestAccess.User("alice");
        var access = new CurrentAccess(new SettableAuthenticationStateProvider(user),
            Evaluator(_ => TestAccess.Scoped((ProjectP, AccessRole.Operator))).Object);

        access.IsReady.Should().BeFalse();
        access.CanRead(ProjectP).Should().BeFalse("nothing is granted before evaluation");

        await access.InitializeAsync();

        access.IsReady.Should().BeTrue();
        access.Username.Should().Be("alice");
        access.CanOperate(ProjectP).Should().BeTrue();
        access.CanRead(Guid.Parse(ProjectP)).Should().BeTrue();
        access.CanRead(ProjectQ).Should().BeFalse();
        access.CanRead((string?)null).Should().BeFalse("objects without a project need a global role");
        access.HasGlobalRead.Should().BeFalse();
    }

    [Fact]
    public async Task CurrentAccess_InitializeAsync_EvaluatesOnce()
    {
        var rbac = Evaluator(_ => TestAccess.Global(AccessRole.ReadOnly));
        var access = new CurrentAccess(new SettableAuthenticationStateProvider(TestAccess.User()), rbac.Object);

        await access.InitializeAsync();
        await access.InitializeAsync();

        rbac.Verify(r => r.EvaluateAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CurrentAccess_ReEvaluates_WhenTheSessionEnds()
    {
        var provider = new SettableAuthenticationStateProvider(TestAccess.User());
        var access = new CurrentAccess(provider,
            Evaluator(p => p.Identity?.IsAuthenticated == true ? TestAccess.Global(AccessRole.Admin) : AccessGrant.None).Object);
        await access.InitializeAsync();
        var changed = false;
        access.Changed += () => changed = true;

        provider.SetUser(new ClaimsPrincipal(new ClaimsIdentity()));
        await access.InitializeAsync();

        changed.Should().BeTrue();
        access.IsAdmin.Should().BeFalse();
        access.Grant.HasAnyAccess.Should().BeFalse();
    }

    [Theory]
    [InlineData(AccessRole.ReadOnly, ProjectP, true)]
    [InlineData(AccessRole.Operator, ProjectP, true)]
    [InlineData(AccessRole.Admin, ProjectP, false)]
    [InlineData(AccessRole.ReadOnly, ProjectQ, false)]
    [InlineData(AccessRole.ReadOnly, null, false)]
    public async Task AccessGuard_AllowsOnlyTheGrantedRoleOnTheGrantedProject(AccessRole required, string? projectId, bool allowed)
    {
        var grant = TestAccess.Scoped((ProjectP, AccessRole.Operator));
        var guard = new AccessGuard(CurrentAccess.CreateLoaded(TestAccess.User(), grant),
            Evaluator(_ => grant).Object, new FakeLogger<AccessGuard>());

        var act = () => guard.DemandAsync(required, projectId, "TestAction");

        if (allowed)
            await act.Should().NotThrowAsync();
        else
            (await act.Should().ThrowAsync<AccessDeniedException>()).Which.Action.Should().Be("TestAction");
    }

    [Fact]
    public async Task AccessGuard_EvaluatesFresh_AndLogsTheDenialWithSanitizedUsername()
    {
        var logger = new FakeLogger<AccessGuard>();
        var user = TestAccess.User("eve\r\nINJECTED");
        // The circuit was rendered as admin, but the evaluation now yields no role (binding removed).
        var guard = new AccessGuard(CurrentAccess.CreateLoaded(user, TestAccess.Global(AccessRole.Admin)),
            Evaluator(_ => AccessGrant.None).Object, logger);

        await FluentActions.Awaiting(() => guard.DemandAsync(AccessRole.Admin, null, "SaveTemplate"))
            .Should().ThrowAsync<AccessDeniedException>();

        var record = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;
        record.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        record.Message.Should().Contain("SaveTemplate").And.NotContain("\r\nINJECTED");
    }

    [Fact]
    public void SessionRevalidation_ValidOnlyBeforeExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AuthPrincipals.Create(IdentitySources.Oidc, "s", "u", null, null, [], now.AddMinutes(1));
        var state = new AuthenticationState(user);

        SessionRevalidatingAuthenticationStateProvider.IsSessionValid(state, now).Should().BeTrue();
        SessionRevalidatingAuthenticationStateProvider.IsSessionValid(state, now.AddMinutes(2)).Should().BeFalse();
        SessionRevalidatingAuthenticationStateProvider.IsSessionValid(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("x"))), now).Should().BeFalse("no expiry claim means no valid session");
    }

    [Fact]
    public void LocalAdminPrincipal_RoundTripsItsClaims()
    {
        var expires = new DateTimeOffset(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);
        var admin = AuthPrincipals.CreateLocalAdmin(expires);

        AuthPrincipals.IsLocalAdmin(admin).Should().BeTrue();
        admin.Identity!.Name.Should().Be("admin");
        AuthPrincipals.GetSessionExpiry(admin).Should().Be(expires);
        AuthPrincipals.GetGroups(admin).Should().BeEmpty();
    }
}
