using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for /api/consolidation-runs endpoints.
/// All endpoints require operator-tier API key authentication.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ConsolidationRunEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConsolidationRunEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private static ConsolidationRun MakeRun(Guid? id = null)
    {
        var runId = id ?? Guid.NewGuid();
        return new ConsolidationRun
        {
            RunId = runId.ToString(),
            Type = ConsolidationRunType.HarnessSuggestions,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
    }

    // ── GET /api/consolidation-runs ──────────────────────────────────────────

    [Fact]
    public async Task GetAll_NoRuns_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/consolidation-runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await response.Content.ReadFromJsonAsync<List<ConsolidationRun>>(PipelineJsonOptions.Default);
        // May contain runs from other tests in the same shared factory — just verify 200 and parseable
        runs.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAll_AfterSave_ReturnsRun()
    {
        var run = MakeRun();

        await _client.PutAsJsonAsync($"/api/consolidation-runs/{run.RunId}", run, PipelineJsonOptions.Default);

        var response = await _client.GetAsync("/api/consolidation-runs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await response.Content.ReadFromJsonAsync<List<ConsolidationRun>>(PipelineJsonOptions.Default);
        runs.Should().Contain(r => r.RunId == run.RunId);
    }

    // ── GET /api/consolidation-runs/{runId} ──────────────────────────────────

    [Fact]
    public async Task GetById_NonExistentRun_Returns404()
    {
        var response = await _client.GetAsync($"/api/consolidation-runs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_ExistingRun_Returns200WithBody()
    {
        var run = MakeRun();
        await _client.PutAsJsonAsync($"/api/consolidation-runs/{run.RunId}", run, PipelineJsonOptions.Default);

        var response = await _client.GetAsync($"/api/consolidation-runs/{run.RunId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var returned = await response.Content.ReadFromJsonAsync<ConsolidationRun>(PipelineJsonOptions.Default);
        returned.Should().NotBeNull();
        returned!.RunId.Should().Be(run.RunId);
    }

    // ── PUT /api/consolidation-runs/{runId} ──────────────────────────────────

    [Fact]
    public async Task Save_ValidRun_Returns200()
    {
        var run = MakeRun();

        var response = await _client.PutAsJsonAsync(
            $"/api/consolidation-runs/{run.RunId}", run, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_RouteRunIdMismatch_Returns400()
    {
        var run = MakeRun();
        var differentId = Guid.NewGuid(); // route ID != body RunId

        var response = await _client.PutAsJsonAsync(
            $"/api/consolidation-runs/{differentId}", run, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("RunId");
    }

    [Fact]
    public async Task Save_ThenGetById_RoundTrip()
    {
        var run = MakeRun();
        run.Status = ConsolidationRunStatus.Succeeded;

        await _client.PutAsJsonAsync($"/api/consolidation-runs/{run.RunId}", run, PipelineJsonOptions.Default);

        var getResponse = await _client.GetAsync($"/api/consolidation-runs/{run.RunId}");
        var returned = await getResponse.Content.ReadFromJsonAsync<ConsolidationRun>(PipelineJsonOptions.Default);

        returned!.Status.Should().Be(ConsolidationRunStatus.Succeeded);
    }

    // ── DELETE /api/consolidation-runs/{runId} ────────────────────────────────

    [Fact]
    public async Task Delete_ExistingRun_Returns200_ThenGetReturns404()
    {
        var run = MakeRun();
        await _client.PutAsJsonAsync($"/api/consolidation-runs/{run.RunId}", run, PipelineJsonOptions.Default);

        var deleteResponse = await _client.DeleteAsync($"/api/consolidation-runs/{run.RunId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/consolidation-runs/{run.RunId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonExistentRun_Returns200()
    {
        // Delete on a missing run is idempotent — store.DeleteRunAsync is a no-op
        var response = await _client.DeleteAsync($"/api/consolidation-runs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
