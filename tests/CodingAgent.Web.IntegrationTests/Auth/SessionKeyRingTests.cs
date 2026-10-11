using System.Net;
using AwesomeAssertions;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.IntegrationTests.Smoke;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodingAgent.Web.IntegrationTests.Auth;

/// <summary>
/// Spec 051 D10: the login cookie is protected with the Data Protection key from the Secret, not
/// with a per-process or Redis key ring. A session therefore survives a restart and works on every
/// replica with the same key, and a rotated key signs everyone out.
/// </summary>
[Collection("SmokeTests")]
public class SessionKeyRingTests
{
    private const string SessionCookieName = "ca_session";

    [Fact]
    public async Task SessionCookie_SurvivesARestart_AndWorksOnAnotherReplica()
    {
        string session;
        using (var first = new CustomWebApplicationFactory())
            session = await SignInAsync(first);

        // A new host: nothing is shared with the first one but the key from the environment.
        using var second = new CustomWebApplicationFactory();
        var response = await GetProfileAsync(second, session);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("data-testid=\"profile-username\"");
    }

    [Fact]
    public async Task SessionCookie_IsRejected_AfterTheKeyIsRotated()
    {
        string session;
        using (var issuer = new CustomWebApplicationFactory())
            session = await SignInAsync(issuer);

        using var rotated = new SettingOverrideWebApplicationFactory("DataProtection__Key", "rotated-data-protection-key-0123456789");
        var response = await GetProfileAsync(rotated, session);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
    }

    /// <summary>Signs in as the local admin and returns the session cookie as a Cookie header value.</summary>
    private static async Task<string> SignInAsync(CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var login = await AuthTestEnvironment.PostLoginAsync(client, "admin", AuthTestEnvironment.AdminPassword, returnUrl: "/healthz");
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The cookie may be split into chunks (ca_session, ca_sessionC1, ...); send them all.
        var chunks = login.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith(SessionCookieName, StringComparison.Ordinal))
            .Select(c => c.Split(';', 2)[0])
            .ToList();
        chunks.Should().NotBeEmpty();
        return string.Join("; ", chunks);
    }

    private static async Task<HttpResponseMessage> GetProfileAsync(CustomWebApplicationFactory factory, string session)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/user");
        request.Headers.Add("Cookie", session);
        return await client.SendAsync(request);
    }
}
