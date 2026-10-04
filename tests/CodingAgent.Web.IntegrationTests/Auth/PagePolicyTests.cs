using System.Net;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>
/// The smoke-test host with role bindings and a test-only <c>/test/sign-in</c> endpoint that issues
/// the real session cookie for an OIDC-shaped principal, so page policies can be exercised for
/// principals other than the local admin without an identity provider.
/// </summary>
public sealed class RbacWebApplicationFactory : CustomWebApplicationFactory
{
    public const string PaymentsProjectId = "6f1c2a9e-0000-0000-0000-00000000000a";

    private static readonly string[] Variables =
    [
        "Auth__Rbac__Bindings__0__User", "Auth__Rbac__Bindings__0__Role",
        "Auth__Rbac__Bindings__1__Group", "Auth__Rbac__Bindings__1__Role", "Auth__Rbac__Bindings__1__Project",
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__0__User", "reader");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__0__Role", "readonly");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__1__Group", "team-a");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__1__Role", "operator");
        Environment.SetEnvironmentVariable("Auth__Rbac__Bindings__1__Project", "payments");

        builder.ConfigureTestServices(services =>
        {
            var projects = new Mock<IProjectStore>();
            projects.Setup(p => p.LoadProjectsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new PipelineProject { Id = PaymentsProjectId, Name = "payments" }]);
            services.RemoveAll<IProjectStore>();
            services.AddSingleton(projects.Object);
            services.AddSingleton<IStartupFilter, TestSignInStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var variable in Variables)
                Environment.SetEnvironmentVariable(variable, null);
        }
        base.Dispose(disposing);
    }

    /// <summary>A client signed in as <paramref name="username"/> with <paramref name="groups"/>.</summary>
    public async Task<HttpClient> SignInAsync(string username, params string[] groups)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var response = await client.GetAsync($"/test/sign-in?user={Uri.EscapeDataString(username)}&groups={Uri.EscapeDataString(string.Join(',', groups))}");
        response.EnsureSuccessStatusCode();
        return client;
    }

    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        // A plain path check, not app.Map: Map moves the path into PathBase, and the cookie
        // handler would then scope the session cookie to /test/sign-in.
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != "/test/sign-in")
                {
                    await nextMiddleware();
                    return;
                }

                var user = context.Request.Query["user"].ToString();
                var groups = context.Request.Query["groups"].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    AuthPrincipals.Create(IdentitySources.Oidc, $"sub-{user}", user, null, null, groups, expiresAt),
                    new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expiresAt });
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };
    }
}

/// <summary>Spec 049 Req 5.9, 7.1: page policies on full page loads for each kind of principal.</summary>
[Collection("SmokeTests")]
public class PagePolicyTests : IClassFixture<RbacWebApplicationFactory>
{
    private readonly RbacWebApplicationFactory _factory;

    public PagePolicyTests(RbacWebApplicationFactory factory) => _factory = factory;

    private static void ShouldBeDenied(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/access-denied");
    }

    [Theory]
    [InlineData("/settings")]
    public async Task GlobalReadOnly_IsDeniedAdminPages(string path)
    {
        var client = await _factory.SignInAsync("reader");

        ShouldBeDenied(await client.GetAsync(path));
    }

    [Theory]
    [InlineData("/fleet")]
    [InlineData("/consolidation")]
    [InlineData("/about")]
    [InlineData("/user")]
    public async Task GlobalReadOnly_OpensReadPages(string path)
    {
        var client = await _factory.SignInAsync("reader");

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GlobalReadOnly_IsDeniedAgentChat()
    {
        var client = await _factory.SignInAsync("reader");

        ShouldBeDenied(await client.GetAsync("/agent-chat"));
    }

    [Theory]
    [InlineData("/fleet")]
    [InlineData("/consolidation")]
    [InlineData("/settings")]
    public async Task ScopedOperator_IsDeniedGlobalPages(string path)
    {
        var client = await _factory.SignInAsync("alice", "team-a");

        ShouldBeDenied(await client.GetAsync(path));
    }

    [Theory]
    [InlineData("/agent-chat")]
    [InlineData("/about")]
    public async Task ScopedOperator_OpensProjectPages(string path)
    {
        var client = await _factory.SignInAsync("alice", "team-a");

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/overview")]
    [InlineData("/about")]
    [InlineData("/fleet")]
    public async Task NoAccessUser_IsDeniedEveryCockpitPage(string path)
    {
        var client = await _factory.SignInAsync("carol");

        ShouldBeDenied(await client.GetAsync(path));
    }

    [Fact]
    public async Task NoAccessUser_SeesItsProfileAndTheNoAccessMessage()
    {
        var client = await _factory.SignInAsync("carol", "unbound-group");

        var profile = await client.GetStringAsync("/user");
        var denied = await client.GetStringAsync("/access-denied");

        profile.Should().Contain("unbound-group");
        denied.Should().Contain("You have no access yet");
    }

    [Fact]
    public async Task ForbiddenPage_RedirectsWithoutLeakingTheLoginPage()
    {
        var client = await _factory.SignInAsync("reader");

        var response = await client.GetAsync("/settings");

        ShouldBeDenied(response);
        response.Headers.Location!.ToString().Should().NotContain("/login");
    }
}
