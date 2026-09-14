using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Integration tests for /api/feedback-comment-outbox endpoints.
/// All endpoints require operator-tier (AgentApiKey) authentication.
/// Uses the shared <see cref="ApiWebApplicationFactory"/> with InMemory EF.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class FeedbackCommentOutboxEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FeedbackCommentOutboxEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── GET /api/feedback-comment-outbox/pending ──────────────────────────

    [Fact]
    public async Task GetPending_EmptyStore_Returns200WithEmptyList()
    {
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var response = await client.GetAsync("/api/feedback-comment-outbox/pending?maxAttempts=5&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<List<FeedbackCommentOutboxEntry>>(PipelineJsonOptions.Default);
        entries.Should().NotBeNull();
        entries!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPending_WithValidParams_Returns200()
    {
        // maxAttempts and pageSize are required query parameters; test with valid values
        var response = await _client.GetAsync("/api/feedback-comment-outbox/pending?maxAttempts=5&pageSize=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPending_ZeroMaxAttempts_DefaultsTo5_Returns200()
    {
        var response = await _client.GetAsync("/api/feedback-comment-outbox/pending?maxAttempts=0&pageSize=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPending_RequiresAuth_Returns401WhenUnauthenticated()
    {
        using var anonClient = _factory.CreateClient();

        var response = await anonClient.GetAsync("/api/feedback-comment-outbox/pending");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/feedback-comment-outbox/{id}/complete ───────────────────

    [Fact]
    public async Task MarkComplete_ExistingEntry_Returns200()
    {
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        // Seed an entry via the store (use the DB context directly)
        var entryId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.FeedbackCommentOutbox.Add(new CodingAgent.Infrastructure.Persistence.Entities.FeedbackCommentOutboxEntity
            {
                Id = entryId,
                RunId = $"run-complete-{entryId:N}",
                IssueProviderConfigId = "github",
                IssueIdentifier = "GH-1",
                RepoProviderConfigId = "github-repo",
                FeedbackJson = "{}",
                Status = "Pending",
                AttemptCount = 0,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/feedback-comment-outbox/{entryId}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkComplete_UnknownId_Returns200()
    {
        // MarkCompletedAsync is idempotent for unknown IDs (no-op)
        var response = await _client.PostAsync($"/api/feedback-comment-outbox/{Guid.NewGuid()}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkComplete_RequiresAuth_Returns401WhenUnauthenticated()
    {
        using var anonClient = _factory.CreateClient();

        var response = await anonClient.PostAsync($"/api/feedback-comment-outbox/{Guid.NewGuid()}/complete", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/feedback-comment-outbox/{id}/fail ───────────────────────

    [Fact]
    public async Task MarkFail_ExistingEntry_Returns200()
    {
        await using var factory = new ApiWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        // Seed an entry
        var entryId = Guid.NewGuid();
        using (var db = factory.CreateDbContext())
        {
            db.FeedbackCommentOutbox.Add(new CodingAgent.Infrastructure.Persistence.Entities.FeedbackCommentOutboxEntity
            {
                Id = entryId,
                RunId = $"run-fail-{entryId:N}",
                IssueProviderConfigId = "github",
                IssueIdentifier = "GH-2",
                RepoProviderConfigId = "github-repo",
                FeedbackJson = "{}",
                Status = "Pending",
                AttemptCount = 0,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var body = new { errorMessage = "provider error", maxAttempts = 5 };
        var response = await client.PostAsJsonAsync($"/api/feedback-comment-outbox/{entryId}/fail", body, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkFail_UnknownId_Returns200()
    {
        // MarkFailedAsync is idempotent for unknown IDs (no-op)
        var body = new { errorMessage = "error", maxAttempts = 5 };
        var response = await _client.PostAsJsonAsync($"/api/feedback-comment-outbox/{Guid.NewGuid()}/fail", body, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkFail_RequiresAuth_Returns401WhenUnauthenticated()
    {
        using var anonClient = _factory.CreateClient();
        var body = new { errorMessage = "error", maxAttempts = 5 };

        var response = await anonClient.PostAsJsonAsync($"/api/feedback-comment-outbox/{Guid.NewGuid()}/fail", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
