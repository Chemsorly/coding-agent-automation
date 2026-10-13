using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 D3: a project the user cannot see answers 404 (its existence is not revealed); a
/// project they can see but not change answers 403.
/// </summary>
public sealed class UserAccessTests
{
    private const string ProjectA = TestUserTokens.ProjectA;
    private const string ProjectB = TestUserTokens.ProjectB;

    private static UserAccess For(AccessGrant? grant)
    {
        var context = new DefaultHttpContext();
        if (grant is not null)
        {
            var claims = new List<Claim> { new(UserTokenClaimTypes.Subject, "oidc:alice"), new(UserTokenClaimTypes.Username, "alice") };
            claims.AddRange(AccessGrantClaims.ToClaims(grant));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "User"));
        }

        return new UserAccess(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void Anonymous_HasNoGrant()
    {
        var access = For(null);

        access.Grant.HasAnyAccess.Should().BeFalse();
        access.Subject.Should().BeNull();
    }

    [Fact]
    public void ReadsTheUserAndGrantFromTheToken()
    {
        var access = For(TestUserTokens.Scoped(ProjectA, AccessRole.Operator));

        access.Subject.Should().Be("oidc:alice");
        access.Username.Should().Be("alice");
        access.CanOperate(ProjectA).Should().BeTrue();
        access.CanRead(ProjectB).Should().BeFalse();
    }

    [Fact]
    public void DemandRead_OnAnInvisibleProject_Is404()
    {
        var act = () => For(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).DemandRead(ProjectB);

        act.Should().Throw<UiAccessDeniedException>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void DemandOperate_OnAReadableProject_Is403()
    {
        var act = () => For(TestUserTokens.Scoped(ProjectA, AccessRole.ReadOnly)).DemandOperate(ProjectA);

        act.Should().Throw<UiAccessDeniedException>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void DemandOperate_OnAnInvisibleProject_Is404_NotForbidden()
    {
        var act = () => For(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).DemandOperate(ProjectB);

        act.Should().Throw<UiAccessDeniedException>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void GlobalRoles_CoverEveryProject_AndGlobalObjects()
    {
        var access = For(TestUserTokens.Global(AccessRole.Operator));

        access.Invoking(a => a.DemandOperate(ProjectB)).Should().NotThrow();
        access.Invoking(a => a.DemandOperate(null)).Should().NotThrow();
    }

    [Fact]
    public void ScopedRoles_DoNotCoverGlobalObjects()
    {
        var act = () => For(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).DemandRead(null);

        act.Should().Throw<UiAccessDeniedException>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DemandList_WithoutAProject_IsForbiddenForAScopedUser(string? projectId)
    {
        var act = () => For(TestUserTokens.Scoped(ProjectA, AccessRole.Admin)).DemandList(projectId);

        act.Should().Throw<UiAccessDeniedException>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        For(TestUserTokens.Global(AccessRole.ReadOnly)).Invoking(a => a.DemandList(projectId)).Should().NotThrow();
    }

    [Fact]
    public void DemandList_ForAProject_NeedsReadOnIt()
    {
        var access = For(TestUserTokens.Scoped(ProjectA, AccessRole.ReadOnly));

        access.Invoking(a => a.DemandList(ProjectA)).Should().NotThrow();
        access.Invoking(a => a.DemandList(ProjectB)).Should().Throw<UiAccessDeniedException>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status403Forbidden)]
    public async Task Filter_TurnsADenialIntoItsStatusCode(int statusCode)
    {
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        var result = await new UiAccessDeniedFilter().InvokeAsync(context, _ => throw new UiAccessDeniedException(statusCode));

        result.Should().BeOfType<StatusCodeHttpResult>().Which.StatusCode.Should().Be(statusCode);
    }
}
