using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 D1–D3: the UI surface (<c>/api/ui/**</c>) accepts only a valid user token, the service
/// surface only API keys, and an endpoint that declares nothing is denied.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiSurfaceTests(ApiWebApplicationFactory factory)
{
    private const string Me = "/api/ui/me";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private HttpClient Client(string? bearer = null, string? agentId = null)
    {
        var client = factory.CreateClient();
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (agentId is not null)
            client.BaseAddress = new Uri(client.BaseAddress!, $"?agentId={agentId}");
        return client;
    }

    [Fact]
    public async Task Me_ReturnsTheTokenUser_AndResolvesProjectNames()
    {
        await factory.Services.GetRequiredService<IProjectStore>()
            .SaveProjectAsync(new PipelineProject { Id = TestUserTokens.ProjectA, Name = "Alpha" }, CancellationToken.None);
        var grant = new AccessGrant(AccessRole.ReadOnly,
            new Dictionary<string, AccessRole> { [TestUserTokens.ProjectA] = AccessRole.Operator, [TestUserTokens.ProjectB] = AccessRole.ReadOnly },
            []);

        var response = await Client(TestUserTokens.For(grant, "oidc:alice", "alice")).GetAsync(Me);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<UiMeResponse>(Json);
        me!.Subject.Should().Be("oidc:alice");
        me.Username.Should().Be("alice");
        me.IdentitySource.Should().Be("oidc");
        me.GlobalRole.Should().Be("readonly");
        me.ProjectRoles.Should().BeEquivalentTo(new[]
        {
            new UiMeProjectRole(TestUserTokens.ProjectA, "Alpha", "operator", true),
            new UiMeProjectRole(TestUserTokens.ProjectB, null, "readonly", false),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Me_ReturnsTheLocalAdmin()
    {
        var me = await Client(TestUserTokens.LocalAdmin()).GetFromJsonAsync<UiMeResponse>(Me, Json);

        me!.Subject.Should().Be("local:admin");
        me.IdentitySource.Should().Be("local");
        me.GlobalRole.Should().Be("admin");
    }

    public static TheoryData<string, string> InvalidTokens => new()
    {
        { "expired", TestUserTokens.Expired(TestUserTokens.Global(AccessRole.Admin)) },
        { "another key", TestUserTokens.SignedWithAnotherKey(TestUserTokens.Global(AccessRole.Admin)) },
        { "unsigned (alg none)", TestUserTokens.Unsigned(TestUserTokens.Global(AccessRole.Admin)) },
        { "another issuer", TestUserTokens.WithIssuer("someone-else", TestUserTokens.Global(AccessRole.Admin)) },
        { "another audience", TestUserTokens.WithAudience("someone-else", TestUserTokens.Global(AccessRole.Admin)) },
        { "garbage", "not-a-token" },
    };

    [Theory]
    [MemberData(nameof(InvalidTokens), DisableDiscoveryEnumeration = true)]
    public async Task Me_RejectsAnInvalidToken(string name, string token)
    {
        (await Client(token).GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, name);
    }

    [Fact]
    public async Task Me_RejectsAnonymous()
    {
        (await Client().GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_RejectsTheMasterKey_AndAnAgentKey()
    {
        (await Client(ApiWebApplicationFactory.ApiKey).GetAsync(Me)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the UI surface accepts no API key");

        var agentKey = AgentKeyDerivation.DeriveAgentKey(ApiWebApplicationFactory.ApiKey, "agent-1");
        (await Client(agentKey, "agent-1").GetAsync(Me)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the UI surface accepts no API key");
    }

    [Fact]
    public async Task Me_RefusesAValidTokenThatGrantsNothing()
    {
        (await Client(TestUserTokens.For(AccessGrant.None)).GetAsync(Me)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceSurface_RejectsAUserToken()
    {
        var admin = Client(TestUserTokens.For(TestUserTokens.Global(AccessRole.Admin)));

        (await admin.GetAsync("/api/agents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.GetAsync("/api/config/provider-configs?kind=Issue")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnEndpointWithoutAPolicy_IsDenied()
    {
        // No endpoint is mapped here, so only the fallback policy applies.
        (await Client().GetAsync("/api/not-mapped")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(ApiWebApplicationFactory.ApiKey).GetAsync("/api/not-mapped")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
