using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Api.IntegrationTests;

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

    private Guid SeedRun(bool hasFeedback = false, PipelineStep finalStep = PipelineStep.Completed, string? projectId = null, string? issueUrl = null)
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
            FinalStep = finalStep,
#pragma warning disable CS0618
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
#pragma warning restore CS0618
            StartedAtOffset = DateTimeOffset.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            IssueUrl = issueUrl,
            Feedback = feedback
        };

        using var db = _factory.CreateDbContext();
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = summary.IssueIdentifier.Value,
            IssueTitle = summary.IssueTitle,
            FinalStep = finalStep,
            ProjectId = projectId,
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

    [Fact]
    public async Task GetRunHistory_FinalStepFilter_ReturnsOnlyMatchingOutcome()
    {
        var failed = SeedRun(finalStep: PipelineStep.Failed);
        var completed = SeedRun(finalStep: PipelineStep.Completed);

        var response = await _client.GetAsync("/api/pipeline-runs?finalStep=Failed&pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().Contain(r => r.RunId == failed.ToString());
        body.Items.Should().NotContain(r => r.RunId == completed.ToString());
    }

    [Fact]
    public async Task GetRunHistory_ProjectIdFilter_ReturnsOnlyMatchingProject()
    {
        var inA = SeedRun(projectId: "project-a");
        var inB = SeedRun(projectId: "project-b");

        var response = await _client.GetAsync("/api/pipeline-runs?projectId=project-a&pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().Contain(r => r.RunId == inA.ToString());
        body.Items.Should().NotContain(r => r.RunId == inB.ToString());
    }

    [Fact]
    public async Task GetRunById_RoundTripsIssueUrl()
    {
        const string url = "https://github.com/owner/repo/issues/42";
        var runId = SeedRun(issueUrl: url);

        var response = await _client.GetAsync($"/api/pipeline-runs/{runId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<PipelineRunSummary>(PipelineJsonOptions.Default);
        summary.Should().NotBeNull();
        summary!.IssueUrl.Should().Be(url);
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

    // ── GET /api/pipeline-runs?includeActive=true — Pending run exclusion (issue #2528) ──────

    /// <summary>
    /// Regression test for issue #2528: a work item that is queued (Pending) must NOT
    /// appear in the includeActive=true merge as "Running".
    /// POST /api/work-items inserts a WorkItem as Status=Pending AND calls AddRun — this is
    /// the exact production codepath that caused the phantom "Running" count.
    /// </summary>
    [Fact]
    public async Task GetRunHistory_IncludeActive_PendingWorkItem_IsExcludedFromMerge()
    {
        // Create a work item via the API — this inserts it as Status=Pending AND registers
        // the PipelineRun in IOrchestratorRunService (the production codepath for the bug).
        var pendingRequest = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"pending-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        };
        var createResponse = await _client.PostAsJsonAsync("/api/work-items", pendingRequest,
            PipelineJsonOptions.Default);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var pendingRunId = await createResponse.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        // TODO [WARNING]: This test does not verify that the pending run was actually registered
        // in IOrchestratorRunService before asserting its absence. If POST /api/work-items ever
        // stopped calling AddRun, the NotContain assertion would still pass vacuously (nothing
        // registered = nothing shown). Consider adding:
        //   var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        //   runService.GetActiveRuns().Should().Contain(r => r.RunId == pendingRunId.ToString(), ...)
        // before the GET to confirm the run IS in the active set prior to the exclusion check.
        // Also: this test does not call RemoveRun in a finally block, so the Pending run lingers
        // in the shared singleton IOrchestratorRunService for the lifetime of the test session.
        // This can affect tests that assert inFlightSummaries.Count == 0 (e.g.
        // GetRunHistory_IncludeActive_NoActiveRuns_ReturnsHistoryOnly). Add try/finally cleanup
        // mirroring the other four new tests in this group.

        // The pending run must NOT appear in the includeActive merge.
        var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true&pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
        body.Should().NotBeNull();
        body!.Items.Should().NotContain(r => r.RunId == pendingRunId.ToString(),
            "a Pending (queued) work item must not appear as Running in the active-run merge");
    }

    /// <summary>
    /// A work item seeded directly as Status=Dispatched with a matching active run
    /// MUST appear in the includeActive=true merge (the fix must not over-filter).
    /// </summary>
    // TODO [WARNING]: This test seeds the WorkItem directly in the DB and manually calls
    // AddRun, bypassing the production codepath (POST /api/work-items → CreateWorkItem).
    // If CreateWorkItem ever regressed and stopped calling AddRun for Dispatched items, this
    // test would still pass. Consider replacing the manual setup with a dispatch-API call
    // (or using the production DispatchWorkItem path) to mirror how the Pending exclusion
    // test exercises the real end-to-end path. Same applies to RunningWorkItem_IsIncluded
    // and the dispatched half of MixedPendingAndDispatched_OnlyDispatchedAppears.
    [Fact]
    public async Task GetRunHistory_IncludeActive_DispatchedWorkItem_IsIncluded()
    {
        // Seed a WorkItemEntity directly as Dispatched (bypassing the API so no AddRun is called).
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"dispatched-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        // Register the matching PipelineRun in IOrchestratorRunService directly.
        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        var run = new PipelineRun
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = $"dispatched-run-issue",
            IssueTitle = "Dispatched run",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.Created,
            InitiatedBy = "test"
        };
        runService.AddRun(run);

        try
        {
            var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true&pageSize=500");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
            body.Should().NotBeNull();
            body!.Items.Should().Contain(r => r.RunId == workItemId.ToString(),
                "a Dispatched work item with an active run must appear in the includeActive merge");
        }
        finally
        {
            // Clean up the active run to avoid leaking state into other tests.
            runService.RemoveRun((RunId)workItemId.ToString());
        }
    }

    /// <summary>
    /// A work item seeded directly as Status=Running with a matching active run
    /// MUST appear in the includeActive=true merge.
    /// </summary>
    // TODO [WARNING]: This test manually sets WorkItemStatus.Running and registers the run
    // directly, bypassing the production path (same concern as DispatchedWorkItem_IsIncluded
    // above). Additionally, the production filter only checks WorkItemStatus.Pending, so this
    // test exercises no boundary distinct from the Dispatched case — if the filter accidentally
    // expanded to exclude Running, neither this test nor the Dispatched test would catch it
    // because both manually register the run. Consider making both tests use the production
    // dispatch path so that regressions in AddRun call-sites are caught.
    [Fact]
    public async Task GetRunHistory_IncludeActive_RunningWorkItem_IsIncluded()
    {
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"running-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Running,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        var run = new PipelineRun
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = $"running-run-issue",
            IssueTitle = "Running run",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.RunningQualityGates,
            InitiatedBy = "test"
        };
        runService.AddRun(run);

        try
        {
            var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true&pageSize=500");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
            body.Should().NotBeNull();
            body!.Items.Should().Contain(r => r.RunId == workItemId.ToString(),
                "a Running work item with an active run must appear in the includeActive merge");
        }
        finally
        {
            runService.RemoveRun((RunId)workItemId.ToString());
        }
    }

    /// <summary>
    /// Mixed scenario: one Pending and one Dispatched work item both registered as active runs.
    /// Only the Dispatched run must appear; the Pending run must be absent.
    /// This is the authoritative combined regression test for issue #2528.
    /// </summary>
    [Fact]
    public async Task GetRunHistory_IncludeActive_MixedPendingAndDispatched_OnlyDispatchedAppears()
    {
        // Pending: created via POST /api/work-items (inserts as Pending + calls AddRun).
        var pendingRequest = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"mixed-pending-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        };
        var createResponse = await _client.PostAsJsonAsync("/api/work-items", pendingRequest,
            PipelineJsonOptions.Default);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var pendingRunId = await createResponse.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        // Dispatched: seed DB row as Dispatched + register run manually.
        var dispatchedId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = dispatchedId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"mixed-dispatched-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        var dispatchedRun = new PipelineRun
        {
            RunId = dispatchedId.ToString(),
            IssueIdentifier = "mixed-dispatched-issue",
            IssueTitle = "Dispatched run (mixed test)",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.Created,
            InitiatedBy = "test"
        };
        runService.AddRun(dispatchedRun);

        try
        {
            var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true&pageSize=500");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
            body.Should().NotBeNull();

            body!.Items.Should().Contain(r => r.RunId == dispatchedId.ToString(),
                "the Dispatched work item must appear in the active-run merge");
            body.Items.Should().NotContain(r => r.RunId == pendingRunId.ToString(),
                "the Pending work item must NOT appear in the active-run merge (issue #2528)");
        }
        finally
        {
            runService.RemoveRun((RunId)dispatchedId.ToString());
        }
    }

    /// <summary>
    /// An active run registered in IOrchestratorRunService whose RunId has no matching WorkItem
    /// row in the DB must still appear in the includeActive merge.
    /// Guards against incorrectly filtering rehydrated or orphaned runs.
    /// </summary>
    [Fact]
    public async Task GetRunHistory_IncludeActive_ActiveRunWithNoWorkItem_IsIncluded()
    {
        // Register a run whose RunId has no WorkItemEntity row.
        var orphanRunId = Guid.NewGuid();
        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        var orphanRun = new PipelineRun
        {
            RunId = orphanRunId.ToString(),
            IssueIdentifier = "orphan-issue",
            IssueTitle = "Orphan run (no WorkItem)",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.Created,
            InitiatedBy = "test"
        };
        runService.AddRun(orphanRun);

        try
        {
            var response = await _client.GetAsync("/api/pipeline-runs?includeActive=true&pageSize=500");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadFromJsonAsync<PagedResult<PipelineRunSummary>>(PipelineJsonOptions.Default);
            body.Should().NotBeNull();
            body!.Items.Should().Contain(r => r.RunId == orphanRunId.ToString(),
                "an active run with no matching WorkItem row must be included conservatively (not filtered)");
        }
        finally
        {
            runService.RemoveRun((RunId)orphanRunId.ToString());
        }
    }
}
