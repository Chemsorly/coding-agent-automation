using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>
/// Stands in for the Pipeline API's <c>GET /api/ui/me</c>: validates the user token with the same
/// rules and key as the API, and names each project after the token's subject, so a page shows
/// whose token reached the API.
/// </summary>
public sealed class UiMeApiStub : HttpMessageHandler
{
    private static readonly Microsoft.IdentityModel.Tokens.TokenValidationParameters Validation =
        UserTokens.CreateValidationParameters(UserTokens.CreateSigningKey(AuthTestEnvironment.UserTokenSigningKey));

    public ConcurrentQueue<string> Subjects { get; } = new();

    public static string ProjectNameSeenBy(string subject) => $"payments-seen-by-{subject}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath != "/api/ui/me")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(request.Headers.Authorization?.Parameter, Validation);
        if (!result.IsValid)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var subject = result.ClaimsIdentity.FindFirst(UserTokenClaimTypes.Subject)!.Value;
        Subjects.Enqueue(subject);
        var grant = AccessGrantClaims.FromClaims(result.ClaimsIdentity.Claims);
        var projects = grant.ProjectRoles
            .Select(p => new UiMeProjectRole(p.Key, ProjectNameSeenBy(subject), AccessRoleNames.ToName(p.Value), Known: true))
            .ToList();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new UiMeResponse(subject, null, null, AccessRoleNames.ToName(grant.GlobalRole), projects)),
        };
    }
}

/// <summary>
/// Spec 051 D2/D6: pages call the API's UI surface as the signed-in user. Prerendering runs in the
/// request's scope, so this covers "prerender calls carry the user" with the real token provider,
/// typed client and the API's validation rules.
/// </summary>
[Collection("SmokeTests")]
public class UserTokenPrerenderTests : IClassFixture<RbacWebApplicationFactory>
{
    private readonly RbacWebApplicationFactory _factory;

    public UserTokenPrerenderTests(RbacWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ProfilePrerender_CallsTheApiAsTheSignedInUser()
    {
        var client = await _factory.SignInAsync("alice", "team-a");

        var html = await client.GetStringAsync("/user");

        html.Should().Contain($"Project {UiMeApiStub.ProjectNameSeenBy("sub-alice")} ({RbacWebApplicationFactory.PaymentsProjectId})");
        _factory.MeApi.Subjects.Should().Contain("sub-alice");
    }

    [Fact]
    public async Task TwoUsersAtOnce_EachPageIsRenderedWithItsOwnUsersToken()
    {
        var alice = await _factory.SignInAsync("alice", "team-a");
        var dave = await _factory.SignInAsync("dave", "team-a");

        var pages = await Task.WhenAll(Enumerable.Range(0, 5).SelectMany(_ => new[]
        {
            alice.GetStringAsync("/user"),
            dave.GetStringAsync("/user"),
        }));

        for (var i = 0; i < pages.Length; i++)
        {
            var (own, other) = i % 2 == 0 ? ("sub-alice", "sub-dave") : ("sub-dave", "sub-alice");
            pages[i].Should().Contain(UiMeApiStub.ProjectNameSeenBy(own)).And.NotContain(UiMeApiStub.ProjectNameSeenBy(other));
        }
    }
}

/// <summary>The smoke-test host with <c>UserToken__SigningKey</c> replaced.</summary>
public sealed class UserTokenKeyWebApplicationFactory(string? signingKey) : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("UserToken__SigningKey", signingKey);
    }
}

/// <summary>Spec 051 1.3: the web host refuses to start without a usable user-token signing key.</summary>
[Collection("SmokeTests")]
public class UserTokenKeyStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("too-short-0123456789")]
    public void Startup_MissingOrShortUserTokenKey_FailsFastWithNamedSetting(string? signingKey)
    {
        using var factory = new UserTokenKeyWebApplicationFactory(signingKey);

        Exception? ex = null;
        var console = CaptureConsole(() => ex = Record.Exception(() => factory.CreateClient()));

        ex.Should().NotBeNull("startup must fail without a usable user-token signing key");
        console.Should().Contain("UserToken:SigningKey");
    }

    /// <summary>Program assigns its own bootstrap logger, whose console sink writes to Console.Out as it is at that moment.</summary>
    private static string CaptureConsole(Action action)
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return captured.ToString();
    }
}
