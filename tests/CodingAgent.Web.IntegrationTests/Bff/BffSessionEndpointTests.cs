using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Bff;
using CodingAgent.Web.IntegrationTests.Auth;
using CodingAgent.Web.IntegrationTests.Helpers;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.IntegrationTests.Bff;

/// <summary>
/// Spec 052 Req 3.1, 9.2, 14.4: <c>GET /bff/session</c> tells the browser who is signed in and
/// what they may do, for every kind of principal, and nothing else. The OIDC case with a real
/// identity provider is part of the Keycloak suite.
/// </summary>
[Collection("SmokeTests")]
public class BffSessionEndpointTests : IClassFixture<BffWebApplicationFactory>
{
    private readonly BffWebApplicationFactory _factory;

    public BffSessionEndpointTests(BffWebApplicationFactory factory) => _factory = factory;

    private static async Task<HttpResponseMessage> GetSessionAsync(BffClient client, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BffSessionEndpoint.Path);
        if (csrf)
            request.Headers.Add(BffRequestGuard.CsrfHeader, "1");
        return await client.Http.SendAsync(request);
    }

    private static async Task<BffSession> ReadSessionAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the answer is per user");
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), UiJsonContext.Default.BffSession)!;
    }

    [Fact]
    public async Task LocalAdmin_GetsItsIdentity_TheAdminGrant_AndTheBuild()
    {
        using var client = _factory.Anonymous();
        var login = await AuthTestEnvironment.PostLoginAsync(client.Http, "admin", AuthTestEnvironment.AdminPassword, "/healthz");
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var session = await ReadSessionAsync(await GetSessionAsync(client));

        session.Subject.Should().Be(AuthPrincipals.LocalAdminSubject);
        session.Username.Should().Be("admin");
        session.IdentitySource.Should().Be(IdentitySources.Local);
        session.Grant.GlobalRole.Should().Be(AccessRole.Admin);
        session.Expires.Should().BeAfter(DateTimeOffset.UtcNow);
        session.Build.Should().NotBeNull();
    }

    [Fact]
    public async Task OidcUser_GetsItsClaims_AndTheGrantWithItsMatchedBinding()
    {
        using var client = await _factory.SignInWithQueryAsync("user=alice&groups=team-a&email=alice%40example.org&name=Alice%20A&ttl=600");

        var session = await ReadSessionAsync(await GetSessionAsync(client));

        session.Subject.Should().Be("sub-alice");
        session.Username.Should().Be("alice");
        session.Email.Should().Be("alice@example.org");
        session.DisplayName.Should().Be("Alice A");
        session.Groups.Should().Equal("team-a");
        session.IdentitySource.Should().Be(IdentitySources.Oidc);
        session.Expires.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(600), TimeSpan.FromSeconds(30));
        session.Grant.IsScoped.Should().BeTrue();
        session.Grant.ProjectRoles.Should().Equal(new Dictionary<string, AccessRole> { [RbacWebApplicationFactory.PaymentsProjectId] = AccessRole.Operator });
        session.Grant.Matches.Should().ContainSingle(m => m.Role == AccessRole.Operator && m.ProjectId == RbacWebApplicationFactory.PaymentsProjectId);
        session.Build.Should().NotBeNull();
    }

    [Fact]
    public async Task GlobalReader_GetsTheReadOnlyGrant()
    {
        using var client = await _factory.SignInClientAsync("reader");

        var session = await ReadSessionAsync(await GetSessionAsync(client));

        session.Grant.GlobalRole.Should().Be(AccessRole.ReadOnly);
        session.Grant.IsScoped.Should().BeFalse();
    }

    [Fact]
    public async Task NoAccessUser_GetsAnEmptyGrant_AndNoBuildDetails()
    {
        using var client = await _factory.SignInClientAsync("carol", "unbound-group");

        var session = await ReadSessionAsync(await GetSessionAsync(client));

        session.Username.Should().Be("carol");
        session.Groups.Should().Equal("unbound-group");
        session.Grant.HasAnyAccess.Should().BeFalse();
        session.Build.Should().BeNull("n7: no build details for a user without access");
    }

    [Fact]
    public async Task ExpiredSession_Is401()
    {
        using var client = await _factory.SignInWithQueryAsync("user=reader&ttl=-60");

        (await GetSessionAsync(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoSession_Is401_NotALoginRedirect()
    {
        using var client = _factory.Anonymous();

        var response = await GetSessionAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheCsrfHeader_Is400()
    {
        using var client = await _factory.SignInClientAsync("reader");

        (await GetSessionAsync(client, csrf: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>T4 on the session: claims and saved tokens outside the record's shape never come back.</summary>
    [Fact]
    public async Task CanaryClaimsAndTokens_NeverAppearInTheAnswer()
    {
        const string canary = "canary-7d1f3a5e-session-secret";
        using var client = await _factory.SignInWithQueryAsync($"user=reader&canary={canary}");

        var response = await GetSessionAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(canary);
        string.Join("\n", response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value)).Should().NotContain(canary);
    }
}
