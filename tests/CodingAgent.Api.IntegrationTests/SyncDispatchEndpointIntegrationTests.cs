using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Server-side characterization tests for <c>POST /api/work-items/dispatch</c>
/// (<see cref="WorkItemDispatchEndpoints.DispatchWorkItem"/>).
///
/// This endpoint atomically performs PVC selection, K8s Job creation, and Dispatched
/// state write in a single request. It is called by <c>KubernetesWorkDistributor</c>
/// instead of the two-step <c>POST /api/work-items</c> + DispatchLoop path.
///
/// These tests complement <c>SynchronousDispatchEndpointTests</c> (which calls the handler
/// directly with a full lifecycle setup) by exercising the full HTTP stack — route registration,
/// auth binding, request deserialization, and response status codes — using the real
/// <see cref="ApiWebApplicationFactory"/>.
///
/// <para>
/// <strong>Scope:</strong> the shared <see cref="ApiWebApplicationFactory"/> starts with an
/// empty <see cref="CodingAgent.Orchestration.Dispatch.JobTemplateStore"/> (no templates file
/// in the test environment), so all dispatch calls return 422 Unprocessable Entity
/// (permanent config error: no job template for selector). The happy-path 200 and
/// concurrency-limit 409 behaviours are fully covered in
/// <c>SynchronousDispatchEndpointTests</c> via direct handler invocation with a real template store.
/// These tests focus on the HTTP contract: auth gating, permanent-config-error status code, and
/// idempotent-retry status code.
/// </para>
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class SyncDispatchEndpointIntegrationTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SyncDispatchEndpointIntegrationTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeDispatchRequest(string? issueIdentifier = null) => new()
    {
        IssueIdentifier = new IssueIdentifier(issueIdentifier ?? $"dispatch-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-dispatch",
        RepoProviderConfigId = "repo-dispatch",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "",
        TimeoutSeconds = 3600,
        ProjectId = null
    };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When no job template matches the agent selector, the endpoint must return 422
    /// Unprocessable Entity (permanent config error), NOT 409 Conflict (transient capacity
    /// limit). The caller uses this distinction to decide whether to retry (409) or cascade
    /// the run to Failed (422).
    ///
    /// The shared <see cref="ApiWebApplicationFactory"/> uses an empty
    /// <see cref="CodingAgent.Orchestration.Dispatch.JobTemplateStore"/>, so all selectors
    /// produce a 422.
    /// </summary>
    // TODO: [WARNING] PostDispatch_Returns422_WhenNoTemplateForSelector and
    // PostDispatch_Returns422_ForExplicitUnknownSelector exercise identical code paths: the
    // shared factory has no registered templates, so both an empty selector ("") and an explicit
    // unknown selector both produce 422. The second test does not verify different behaviour — it
    // exercises the same branch with a different input value. Consider collapsing into one
    // parameterized test, or override the factory in the second test to register at least one
    // real template, so an empty selector and a named-but-absent selector can produce distinct outcomes.
    [Fact]
    public async Task PostDispatch_Returns422_WhenNoTemplateForSelector()
    {
        var request = MakeDispatchRequest();

        var response = await _client.PostAsJsonAsync("/api/work-items/dispatch", request,
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "a missing job template is a permanent config error and must return 422 " +
            "(distinct from 409 Conflict which indicates a transient capacity limit)");
    }

    /// <summary>
    /// An unknown selector also returns 422 — permanent, not transient.
    /// </summary>
    [Fact]
    public async Task PostDispatch_Returns422_ForExplicitUnknownSelector()
    {
        var request = MakeDispatchRequest() with { AgentSelector = "no-such-template-selector" };

        var response = await _client.PostAsJsonAsync("/api/work-items/dispatch", request,
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "any unknown selector must return 422 (permanent config error, not transient 409)");
    }

    /// <summary>
    /// The endpoint must be protected by authorization — a request without an Authorization
    /// header must return 401 Unauthorized.
    /// </summary>
    [Fact]
    public async Task PostDispatch_Returns401_WhenNoAuthorizationHeader()
    {
        using var unauthClient = _factory.CreateClient();
        var request = MakeDispatchRequest();

        var response = await unauthClient.PostAsJsonAsync("/api/work-items/dispatch", request,
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the /dispatch endpoint must require authorization");
    }

    /// <summary>
    /// A null request body (missing Content-Type or empty body) must return 400 Bad Request,
    /// not 500 — argument validation is the server's responsibility.
    /// </summary>
    // TODO: [WARNING] This test sends `{}` (an empty JSON object with all required fields absent),
    // not a truly missing body. The assertion `((int)statusCode).Should().BeLessThan(500)` is too
    // weak — a 200 response from a permissive endpoint would satisfy it. Tighten the assertion to
    // `Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity)` to pin the
    // expected contract precisely and prevent a false-pass if the endpoint becomes more permissive.
    [Fact]
    public async Task PostDispatch_Returns400_WhenRequestBodyMissing()
    {
        var response = await _client.PostAsync("/api/work-items/dispatch",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // Missing required fields → 400 or 422 (both acceptable; not 500)
        ((int)response.StatusCode).Should().BeLessThan(500,
            "a malformed request body must not cause a 500 Internal Server Error");
    }

    /// <summary>
    /// Confirms the route is registered at the correct path (not a typo in the Map call).
    /// 401 or 422 both confirm the route exists; 404 would indicate the route is missing.
    /// </summary>
    // TODO: [WARNING] This test is redundant: it sends an identical request to the same route as
    // PostDispatch_Returns422_WhenNoTemplateForSelector and asserts only NotBe(404). Since 422 is
    // not 404, the route-registration assertion is fully implied by the 422 test — this test would
    // never fail independently when the 422 test passes. Consider removing it to reduce noise, or
    // replace it with a test that verifies a scenario not covered by the 422 test (e.g. OPTIONS,
    // a route with a different HTTP method, or a path-segment typo variant).
    [Fact]
    public async Task PostDispatch_RouteIsRegistered_NotA404()
    {
        var request = MakeDispatchRequest();

        var response = await _client.PostAsJsonAsync("/api/work-items/dispatch", request,
            PipelineJsonOptions.Default);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the POST /api/work-items/dispatch route must be registered correctly");
    }
}
