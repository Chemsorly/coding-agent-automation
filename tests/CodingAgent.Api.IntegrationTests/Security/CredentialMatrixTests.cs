using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgent.Api.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CredentialMatrixCollection : ICollectionFixture<AuthOnlyApiFactory>
{
    public const string Name = "CredentialMatrix";
}

/// <summary>
/// The API host with one change: when authorization succeeds the request is answered with
/// <see cref="AuthOnlyResultHandler.Passed"/> instead of running the endpoint. The matrix then
/// tests authorization alone — no handler runs, mutates state or fails on test-double data.
/// </summary>
public sealed class AuthOnlyApiFactory : ApiWebApplicationFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IAuthorizationMiddlewareResultHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthOnlyResultHandler>();
    }
}

public sealed class AuthOnlyResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const int Passed = 299;

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded)
            return _default.HandleAsync(next, context, policy, authorizeResult);

        context.Response.StatusCode = Passed;
        return Task.CompletedTask;
    }
}

/// <summary>
/// T2 (spec 051): every endpoint, called with every kind of credential, answers exactly the status
/// its declared policies imply — 401, 403, or passed. Anything else (including any 5xx) fails.
/// </summary>
[Collection(CredentialMatrixCollection.Name)]
public sealed class CredentialMatrixTests(AuthOnlyApiFactory factory)
{
    private const string MatrixAgentId = "matrix-agent";

    private enum Kind { Anonymous, InvalidCredential, MasterKey, AgentKey, User }

    private sealed record Principal(string Name, Kind Kind, string? Bearer, AccessGrant? Grant = null);

    private static IReadOnlyList<Principal> Principals()
    {
        var admin = TestUserTokens.Global(AccessRole.Admin);
        return
        [
            new("anonymous", Kind.Anonymous, null),
            new("garbage bearer", Kind.InvalidCredential, "garbage"),
            new("expired token", Kind.InvalidCredential, TestUserTokens.Expired(admin)),
            new("token signed with another key", Kind.InvalidCredential, TestUserTokens.SignedWithAnotherKey(admin)),
            new("unsigned token", Kind.InvalidCredential, TestUserTokens.Unsigned(admin)),
            new("token from another issuer", Kind.InvalidCredential, TestUserTokens.WithIssuer("someone-else", admin)),
            new("token for another audience", Kind.InvalidCredential, TestUserTokens.WithAudience("someone-else", admin)),
            new("master key", Kind.MasterKey, ApiWebApplicationFactory.ApiKey),
            new("agent key", Kind.AgentKey, AgentKeyDerivation.DeriveAgentKey(ApiWebApplicationFactory.ApiKey, MatrixAgentId)),
            new("user without roles", Kind.User, TestUserTokens.For(AccessGrant.None), AccessGrant.None),
            User("global readonly", TestUserTokens.Global(AccessRole.ReadOnly)),
            User("global operator", TestUserTokens.Global(AccessRole.Operator)),
            User("global admin", admin),
            User("readonly on A", TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.ReadOnly)),
            User("operator on A", TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.Operator)),
            new("local admin", Kind.User, TestUserTokens.LocalAdmin(), admin),
        ];
    }

    private static Principal User(string name, AccessGrant grant) => new(name, Kind.User, TestUserTokens.For(grant), grant);

    [Fact]
    public async Task EveryEndpoint_AnswersEveryCredential_AsItsPoliciesDeclare()
    {
        var endpoints = EndpointInventory.Read(factory.Services).Where(e => e.Class != EndpointClass.Anonymous).ToList();
        endpoints.Should().NotBeEmpty();
        var principals = Principals();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var mismatches = new List<string>();
        foreach (var endpoint in endpoints)
        {
            foreach (var principal in principals)
            {
                var expected = Expected(endpoint, principal);
                var actual = await SendAsync(client, endpoint, principal);
                if (actual != expected)
                    mismatches.Add($"{endpoint.Method} {endpoint.Route} as {principal.Name}: expected {expected}, got {actual}");
            }
        }

        mismatches.Should().BeEmpty();
    }

    private static async Task<int> SendAsync(HttpClient client, EndpointAuth endpoint, Principal principal)
    {
        var path = endpoint.SamplePath() + (principal.Kind == Kind.AgentKey ? $"?agentId={MatrixAgentId}" : "");
        var method = endpoint.Method == "*" ? HttpMethod.Get : new HttpMethod(endpoint.Method);
        using var request = new HttpRequestMessage(method, path);
        if (principal.Bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", principal.Bearer);
        using var response = await client.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static int Expected(EndpointAuth endpoint, Principal principal) => endpoint.Class switch
    {
        EndpointClass.Service => principal.Kind switch
        {
            Kind.MasterKey => AuthOnlyResultHandler.Passed,
            Kind.AgentKey => endpoint.Policies.Contains(ApiAuthPolicies.Operator) ? 403 : AuthOnlyResultHandler.Passed,
            _ => 401,
        },
        EndpointClass.Ui => principal.Kind == Kind.User
            ? endpoint.Policies.All(p => AccessPolicyRules.IsSatisfied(principal.Grant!, UiPolicies.All[p])) ? AuthOnlyResultHandler.Passed : 403
            : 401,
        // Undeclared and Mixed fail EndpointAuthorizationInventoryTests; expect the deny fallback here.
        _ => principal.Kind is Kind.MasterKey or Kind.AgentKey ? 403 : 401,
    };
}
