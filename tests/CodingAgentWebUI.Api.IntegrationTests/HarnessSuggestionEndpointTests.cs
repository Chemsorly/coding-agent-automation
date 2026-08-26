using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for /api/harness-suggestions endpoints.
/// All endpoints require operator-tier API key authentication.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class HarnessSuggestionEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HarnessSuggestionEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private static HarnessSuggestions MakeSuggestions() => new()
    {
        BasedOnRunCount = 3,
        GeneratedAtUtc = DateTime.UtcNow,
        SuccessRate = 0.75m,
        Suggestions =
        [
            new HarnessSuggestion
            {
                Frequency = 2,
                Rationale = "Frequent failure reason",
                Text = "Use structured logging"
            }
        ]
    };

    // ── GET /api/harness-suggestions ─────────────────────────────────────────

    [Fact]
    public async Task Get_WhenNoSuggestionsStored_Returns204()
    {
        // Fresh factory instance per test collection, but other tests may have stored data.
        // Use a fresh factory to guarantee clean state.
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var response = await client.GetAsync("/api/harness-suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_AfterSave_Returns200WithBody()
    {
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var suggestions = MakeSuggestions();
        await client.PutAsJsonAsync("/api/harness-suggestions", suggestions, PipelineJsonOptions.Default);

        var response = await client.GetAsync("/api/harness-suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var returned = await response.Content.ReadFromJsonAsync<HarnessSuggestions>(PipelineJsonOptions.Default);
        returned.Should().NotBeNull();
        returned!.Suggestions.Should().HaveCount(1);
        returned.Suggestions[0].Text.Should().Be("Use structured logging");
    }

    // ── PUT /api/harness-suggestions ─────────────────────────────────────────

    [Fact]
    public async Task Save_ValidSuggestions_Returns200()
    {
        var suggestions = MakeSuggestions();

        var response = await _client.PutAsJsonAsync(
            "/api/harness-suggestions", suggestions, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_ThenGet_RoundTrip()
    {
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 3,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.75m,
            Suggestions =
            [
                new HarnessSuggestion
                {
                    Frequency = 2,
                    Rationale = "Unique roundtrip reason",
                    Text = "Unique roundtrip test"
                }
            ]
        };

        await client.PutAsJsonAsync("/api/harness-suggestions", suggestions, PipelineJsonOptions.Default);
        var getResponse = await client.GetAsync("/api/harness-suggestions");
        var returned = await getResponse.Content.ReadFromJsonAsync<HarnessSuggestions>(PipelineJsonOptions.Default);

        returned!.Suggestions[0].Text.Should().Be("Unique roundtrip test");
    }
}
