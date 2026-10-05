using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgent.Web.UnitTests.Auth;

/// <summary>Spec 049 Req 3, 10: the login page.</summary>
public class LoginTests : BunitContext
{
    public LoginTests()
    {
        // OIDC only: the password form's antiforgery token is not under test here.
        Services.AddSingleton(Options.Create(new AuthOptions
        {
            Admin = { Enabled = false },
            Oidc = { Enabled = true, Name = "Keycloak" },
        }));
    }

    private IRenderedComponent<Login> RenderLogin(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<Login>(parameters => parameters.AddCascadingValue<HttpContext>(new DefaultHttpContext()));
    }

    [Fact]
    public void OidcLink_IsAFullPageLoad_NotEnhancedNavigation()
    {
        var link = RenderLogin("http://localhost/login?returnUrl=%2Fruns").Find("[data-testid=login-oidc]");

        // /auth/oidc redirects to the provider; an enhanced fetch would run the challenge twice.
        link.GetAttribute("data-enhance-nav").Should().Be("false");
        link.GetAttribute("href").Should().Be("auth/oidc?returnUrl=%2Fruns");
        link.TextContent.Should().Be("Sign in with Keycloak");
    }

    [Fact]
    public void OidcError_ShowsTheProviderFailureMessage()
    {
        var cut = RenderLogin("http://localhost/login?error=oidc");

        cut.Find("[data-testid=login-error]").TextContent.Should().Be("Sign-in with Keycloak failed");
    }
}
