using System.Runtime.CompilerServices;
using System.Security.Claims;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Api.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// T1 (spec 051): every endpoint of the Pipeline API declares exactly one kind of credential, and
/// the full list is pinned in <c>api-endpoint-authorization.snapshot.txt</c>. A new or changed
/// endpoint changes the snapshot, so its authorization shows up in the PR diff.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class EndpointAuthorizationInventoryTests(ApiWebApplicationFactory factory)
{
    private const string SnapshotFile = "api-endpoint-authorization.snapshot.txt";

    private static readonly HashSet<string> AnonymousAllowed = new(StringComparer.Ordinal) { "/healthz", "/readyz" };

    private IReadOnlyList<EndpointAuth> Endpoints => EndpointInventory.Read(factory.Services);

    [Fact]
    public void Inventory_MatchesTheSnapshot()
    {
        var actual = string.Join("\n", Endpoints.Select(e => e.Describe())) + "\n";
        var snapshotPath = Path.Combine(SourceDirectory(), SnapshotFile);
        var expected = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath).ReplaceLineEndings("\n") : "";

        if (actual != expected)
        {
            File.WriteAllText(Path.ChangeExtension(snapshotPath, ".received.txt"), actual);
            actual.Should().Be(expected,
                $"the endpoint authorization changed. Review {Path.GetFileName(Path.ChangeExtension(snapshotPath, ".received.txt"))} " +
                $"next to the snapshot and, if every line is intended, copy it over {SnapshotFile}");
        }
    }

    [Fact]
    public void EveryEndpoint_DeclaresExactlyOneKindOfCredential()
    {
        Endpoints.Where(e => e.Class is EndpointClass.Undeclared or EndpointClass.Mixed)
            .Select(e => e.Describe())
            .Should().BeEmpty("an endpoint must declare API-key policies or UI policies, not none and not both");
    }

    [Fact]
    public void OnlyTheHealthProbes_AreAnonymous()
    {
        Endpoints.Where(e => e.Class == EndpointClass.Anonymous && !AnonymousAllowed.Contains(e.Route))
            .Select(e => e.Describe())
            .Should().BeEmpty();
    }

    [Fact]
    public void TheUiSurface_AndOnlyTheUiSurface_AcceptsTheUserToken()
    {
        static bool OnUiSurface(EndpointAuth e) =>
            e.Route.StartsWith(UiEndpoints.Prefix + "/", StringComparison.Ordinal) || e.Route.StartsWith("/hubs/ui", StringComparison.Ordinal);

        Endpoints.Where(e => OnUiSurface(e) && e.Class != EndpointClass.Ui)
            .Select(e => e.Describe())
            .Should().BeEmpty("nothing under /api/ui or /hubs/ui may accept an API key");
        Endpoints.Where(e => !OnUiSurface(e) && e.Class == EndpointClass.Ui)
            .Select(e => e.Describe())
            .Should().BeEmpty("a user token is only accepted under /api/ui and /hubs/ui");
    }

    /// <summary>
    /// Spec 052 Req 14.10: each <c>/api/ui</c> endpoint carries the surface policy (<see cref="UiPolicies.AnyAccess"/>,
    /// set on the group) and at most one role policy, so its required role is one entry in the snapshot.
    /// </summary>
    [Fact]
    public void EveryUiApiEndpoint_HasTheSurfacePolicy_AndAtMostOneRolePolicy()
    {
        var ui = Endpoints.Where(e => e.Route.StartsWith(UiEndpoints.Prefix + "/", StringComparison.Ordinal)).ToList();

        ui.Should().NotBeEmpty();
        ui.Where(e => !e.Policies.Contains(UiPolicies.AnyAccess) || e.Policies.Count(p => p != UiPolicies.AnyAccess) > 1)
            .Select(e => e.Describe())
            .Should().BeEmpty();
    }

    /// <summary>
    /// The fallback policy fails closed for everything but a SignalR hub method call: since .NET 11 SignalR checks
    /// hub method calls against it, so it passes <see cref="HubInvocationContext"/> (the connection already passed
    /// the hub endpoint's own policy). An HTTP request is authorized with the <see cref="HttpContext"/> as resource
    /// and must stay denied, even for the master key. The hub-call half is covered by AgentHubGateTests and UiHubTests.
    /// </summary>
    [Fact]
    public async Task FallbackPolicy_DeniesEveryResourceButAHubMethodCall()
    {
        var fallback = await factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        fallback.Should().NotBeNull("an endpoint mapped without a policy must not be open");
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        var operatorKey = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("auth_kind", "operator")], AgentApiKeyDefaults.AuthenticationScheme));

        foreach (var resource in new object?[] { new DefaultHttpContext(), null, new object() })
            (await authorization.AuthorizeAsync(operatorKey, resource, fallback!)).Succeeded
                .Should().BeFalse($"the fallback policy must deny resource {resource?.GetType().Name ?? "null"}");
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
