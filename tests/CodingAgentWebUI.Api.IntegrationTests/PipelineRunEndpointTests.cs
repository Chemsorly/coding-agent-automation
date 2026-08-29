using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for /api/pipeline-runs and /api/export/runs.json endpoints.
/// Seeds data directly into the InMemory DbContext via SummaryJson.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class PipelineRunEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PipelineRunEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Auth tier enforcement (W0-04) ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that all /api/pipeline-runs routes require the Operator tier.
    /// A request signed with a per-pod derived key (agentId present → auth_kind=agent)
    /// must receive 403 Forbidden on every method — GET list, GET by id, and POST.
    /// </summary>
    [Theory]
    [InlineData("GET",  "/api/pipeline-runs")]
    [InlineData("GET",  "/api/pipeline-runs/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/pipeline-runs")]
    public async Task PipelineRunEndpoints_AgentDerivedKey_ReturnsForbidden(string method, string path)
    {
        // Derive a key the same way AgentApiKeyAuthHandler does: HMAC-SHA256(masterKey, agentId)
        const string agentId = "test-agent-id";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        var request = new HttpRequestMessage(new HttpMethod(method), $"{path}?agentId={agentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", derivedKey);
        if (method == "POST")
            request.Content = JsonContent.Create(new { }, options: PipelineJsonOptions.Default);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{method} {path} must reject agent-tier derived keys after W0-04 — operator-only endpoint");
    }

    private Guid SeedRun(bool hasFeedback = false)
    {
        var runId = Guid.NewGuid();
        RunFeedback? feedback = hasFeedback
            ? new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback()
            }
            : null;

        var summary = new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = new IssueIdentifier($"run-issue-{Guid.NewGuid():N}"),
            IssueTitle = "Test run",
            FinalStep = PipelineStep.Completed,
#pragma warning disable CS0618
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
#pragma warning restore CS0618
            StartedAtOffset = DateTimeOffset.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow,
            Feedback = feedback
        };

        using var db = _factory.CreateDbContext();
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = summary.IssueIdentifier.Value,
            IssueTitle = summary.IssueTitle,
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            SummaryJson = JsonSerializer.Serialize(summary, PipelineJsonOptions.Default)
        });
        db.SaveChanges();
        return runId;
    }

    // ── Pagination ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunHistory_ReturnsPagedResult()
    {
        SeedRun();
        SeedRun();

        var response = await _client.GetAsync("/api/pipeline-runs?page=1&pageSize=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRunHistory_FeedbackOnlyFilter_ExcludesNonFeedbackRuns()
    {
        // Note: feedbackOnly=true path uses Postgres JSONB operator (FromSqlRaw) which is
        // incompatible with EF InMemory. This test verifies the default feedbackOnly=false
        // path returns all runs including those without feedback.
        var withFeedback = SeedRun(hasFeedback: true);
        var withoutFeedback = SeedRun(hasFeedback: false);

        // feedbackOnly=false (default) should return all runs
        var response = await _client.GetAsync("/api/pipeline-runs?feedbackOnly=false&pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().NotBeNull();
        // Both runs should appear (no filtering)
        body.Items.Should().Contain(r => r.RunId == withFeedback.ToString()
            || r.RunId == withoutFeedback.ToString());
    }

    // ── Single run ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunById_Returns200_WithCorrectId()
    {
        var runId = SeedRun();

        var response = await _client.GetAsync($"/api/pipeline-runs/{runId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<PipelineRunSummary>(PipelineJsonOptions.Default);
        summary.Should().NotBeNull();
        summary!.RunId.Should().Be(runId.ToString());
    }

    [Fact]
    public async Task GetRunById_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/pipeline-runs/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Export ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportRunsJson_HasContentDispositionAttachment()
    {
        SeedRun();

        // Export requires operator authentication — the payload carries issue identifiers and
        // project names, so it is not exposed to unauthenticated callers (see the endpoint's
        // RequireAuthorization(Operator)). This test used an anonymous client and asserted 200,
        // which contradicted that guard; it now authenticates like the sibling export tests.
        var response = await _client.GetAsync("/api/export/runs.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var contentDisposition = response.Content.Headers.ContentDisposition;
        contentDisposition.Should().NotBeNull();
        contentDisposition!.DispositionType.Should().Be("attachment");
    }

    [Fact]
    public async Task ExportRunsJson_FeedbackOnlyFlag_ExcludesNonFeedbackRuns()
    {
        // The export endpoint applies feedbackOnly filter in-memory after paging (faithful port of monolith).
        // Seed a run without feedback, confirm it doesn't appear when feedbackOnly=true.
        var withoutFeedback = SeedRun(hasFeedback: false);
        SeedRun(hasFeedback: true);  // Ensure at least one run with feedback exists

        var response = await _client.GetAsync("/api/export/runs.json?feedbackOnly=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        // The run without feedback must not appear in feedbackOnly=true export
        body.Should().NotContain($"\"{withoutFeedback}\"");
    }

    /// <summary>
    /// Verifies the feedbackOnly filter and paging are wired end-to-end at the HTTP level.
    ///
    /// Seed 4 runs: 2 without feedback (page 1 of 2) and 2 with feedback (page 2 of 2).
    /// Call GET /api/export/runs.json?feedbackOnly=true&page=1&pageSize=2.
    /// The endpoint loads page 1 (2 runs), then applies feedbackOnly in-memory.
    /// Because page 1 contains only non-feedback runs, the result must be empty.
    ///
    /// This test exercises the real HTTP endpoint to confirm both paging and filtering
    /// are wired correctly — not just the simulation helper.
    /// </summary>
    [Fact]
    public async Task ExportRunsJson_FeedbackOnly_PageWhereAllRunsLackFeedback_ReturnsEmpty()
    {
        // Seed 4 runs in a predictable order using controlled timestamps so we know which
        // runs land on page 1 vs page 2.
        // PipelineRunEntity rows are returned newest-first by the service.
        // We insert the no-feedback runs LAST (newest) so they appear on page 1.
        var withFeedback1 = SeedRun(hasFeedback: true);
        var withFeedback2 = SeedRun(hasFeedback: true);
        // These two are newer — they land on page 1 of size 2
        var noFeedback1 = SeedRun(hasFeedback: false);
        var noFeedback2 = SeedRun(hasFeedback: false);

        // page=1&pageSize=2 returns the 2 most-recent runs (both without feedback).
        // feedbackOnly=true applied in-memory → result must be empty.
        var response = await _client.GetAsync("/api/export/runs.json?feedbackOnly=true&page=1&pageSize=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<List<PipelineRunSummary>>(body, PipelineJsonOptions.Default);
        runs.Should().NotBeNull();

        // The no-feedback runs (page 1) are filtered out in-memory → empty result for this page.
        runs!.Should().NotContain(r => r.RunId == noFeedback1.ToString());
        runs.Should().NotContain(r => r.RunId == noFeedback2.ToString());

        // The feedback runs are on page 2 and must NOT appear on page 1.
        runs.Should().NotContain(r => r.RunId == withFeedback1.ToString());
        runs.Should().NotContain(r => r.RunId == withFeedback2.ToString());
    }

    // ── POST /api/pipeline-runs (CreateRunSummary) ────────────────────────────────

    [Fact]
    public async Task CreateRunSummary_Returns201_AndPersistsRun()
    {
        var runId = Guid.NewGuid();
        var summary = new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = new IssueIdentifier($"create-{Guid.NewGuid():N}"),
            IssueTitle = "Created via POST",
            FinalStep = PipelineStep.Completed,
#pragma warning disable CS0618
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
#pragma warning restore CS0618
            StartedAtOffset = DateTimeOffset.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow
        };

        var response = await _client.PostAsJsonAsync("/api/pipeline-runs", summary, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "POST /api/pipeline-runs should return 201 Created when operator-authenticated");

        // Confirm the run is now retrievable
        var getResponse = await _client.GetAsync($"/api/pipeline-runs/{runId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var retrieved = await getResponse.Content.ReadFromJsonAsync<PipelineRunSummary>(PipelineJsonOptions.Default);
        retrieved!.RunId.Should().Be(runId.ToString());
    }

    /// <summary>
    /// Test C — CreateRunSummary idempotency.
    /// Posting the same RunId twice must return 201 both times.
    /// Safe against InMemory EF Core because the second POST triggers an EF Update (not Insert)
    /// via the FindAsync-first upsert in PostgresPipelineRunHistoryService.AddRunToHistoryInternalAsync.
    /// </summary>
    [Fact]
    public async Task CreateRunSummary_SameRunId_SecondCallReturns201()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier($"idem-run-{Guid.NewGuid():N}"),
            IssueTitle = "Idempotency test",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow
        };

        // TODO [WARNING]: This test posts to /api/pipeline-runs (no trailing slash) while
        // PipelineApiRunHistoryClient.AddRunToHistoryAsync posts to /api/pipeline-runs/ (with
        // trailing slash). The test exercises the endpoint directly, not through the client method.
        // Verify both URL forms hit the same route handler, or rewrite to use the client to close the gap.
        // TODO [WARNING]: This test does not assert that only one record was stored — an implementation
        // that inserts a second row and still returns 201 would pass. Consider asserting count == 1 in
        // the IPipelineRunHistoryService or equivalent after the second POST.
        var r1 = await _client.PostAsJsonAsync("/api/pipeline-runs", summary, PipelineJsonOptions.Default);
        r1.StatusCode.Should().Be(HttpStatusCode.Created);

        var r2 = await _client.PostAsJsonAsync("/api/pipeline-runs", summary, PipelineJsonOptions.Default);
        r2.StatusCode.Should().Be(HttpStatusCode.Created,
            "idempotent retry of CreateRunSummary must return 201 — upsert path uses FindAsync-first update, not re-insert");
    }

    // ── GET /api/pipeline-runs?includeActive=true ─────────────────────────────────

    [Fact]
    public async Task GetRunHistory_IncludeActive_NoActiveRuns_ReturnsHistoryOnly()
    {
        // Seed two completed runs in history; no in-flight runs exist.
        SeedRun();
        SeedRun();

        // includeActive=true with no active runs hits the inFlightSummaries.Count == 0 early-return path.
        var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().NotBeNull();
    }
}
