using System.Net.Http.Json;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Shared seed helpers and factories for triage E2E tests. Used by TriageFormAndListTests
/// and later issues #4134 / #4135 that reuse these page objects.
/// </summary>
internal static class TriageTestData
{
    public const string AgentLabel = "triage-e2e";
    public const string TemplateId = "template-triage";
    public const string TemplateName = "checkout-api";

    /// <summary>
    /// Saves an enabled template "checkout-api" (issue-e2e, repo-e2e) on <paramref name="projectId"/>
    /// (defaults to <see cref="WellKnownIds.DefaultProjectId"/>) and routes every task type to a
    /// profile on job template "triage-e2e". Same seed as TriageE2ETests.SeedTemplateAndProfileAsync.
    /// </summary>
    public static async Task SeedTemplateAndRouteAsync(E2EFixture fixture, string? projectId = null)
    {
        var pid = projectId ?? WellKnownIds.DefaultProjectId;
        await fixture.ConfigStore.SaveTemplateAsync((ProjectId)pid, new PipelineJobTemplate
        {
            Id = TemplateId,
            Name = TemplateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);

        await fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-triage",
            DisplayName = "Triage profile",
            JobTemplate = E2ERouting.Key([AgentLabel]),
            Enabled = true,
        });
    }

    /// <summary>Builds a <see cref="TriageDraft"/> with <c>TargetRepository = "checkout-api"</c>.</summary>
    public static TriageDraft Draft(string id, string title, string body = "Fix body text.") =>
        new TriageDraft
        {
            Id = id,
            Kind = TriageDraftKind.RootFix,
            TargetRepository = TemplateName,
            Title = title,
            Body = body,
        };

    /// <summary>
    /// Builds a <see cref="TriageResult"/> with verdict <c>CauseFound</c>, confidence <c>High</c>,
    /// one Investigated entry, one Evidence entry, and the given drafts.
    /// </summary>
    public static TriageResult CauseFound(params TriageDraft[] drafts) =>
        new TriageResult
        {
            Verdict = TriageVerdict.CauseFound,
            Confidence = TriageConfidence.High,
            Summary = "The retry policy re-sends a non-idempotent POST and exhausts the database pool.",
            Investigated =
            [
                new TriageCheck { Check = "5xx rate", Where = "grafana · Prometheus", Result = "jump at 14:03" },
            ],
            Evidence =
            [
                new TriageEvidence { Id = "E1", Claim = "Pool wait p99 29.8 s", Source = "grafana" },
            ],
            Drafts = drafts,
        };

    /// <summary>
    /// POSTs a <see cref="CreateTriageRequest"/> to <c>/api/ui/triages</c> and returns the created
    /// <see cref="TriageDetail"/>.
    /// </summary>
    public static async Task<TriageDetail> StartOperatorTriageAsync(
        HttpClient api, string title, string? projectId = null)
    {
        var response = await api.PostAsJsonAsync("/api/ui/triages", new CreateTriageRequest
        {
            ProjectId = projectId ?? WellKnownIds.DefaultProjectId,
            Request = new TriageRequest
            {
                Title = title,
                WhatHappened = "Observed unexpected behaviour.",
                Expected = "Expected normal behaviour.",
            },
        }, PipelineJsonOptions.Default);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;
    }

    /// <summary>
    /// Waits for <paramref name="agent"/> to receive a job assignment (30 s), then accepts the job,
    /// reports Investigating, reports the triage result, and reports completion.
    /// Mirrors the sequence in TriageE2ETests.cs:92-100.
    /// </summary>
    public static async Task ReportResultAsync(FakeAgentClient agent, TriageResult result)
    {
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Investigating);
        await agent.ReportTriageResultAsync(assignment.JobId, result);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Polls <c>GET /api/ui/triages/{id}</c> every 200 ms for up to 30 s until
    /// <c>Status == expected</c>, then returns the detail.
    /// </summary>
    public static async Task<TriageDetail> WaitForStatusAsync(
        HttpClient api, Guid id, TriageStatus expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var detail = await api.GetFromJsonAsync<TriageDetail>(
                $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
            if (detail?.Status == expected)
                return detail;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Triage {id} did not reach status {expected} within 30 s");
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Inserts a <see cref="TriageRecord"/> directly via <see cref="TriageEntityMapper.ToEntity"/>
    /// into the E2E database. Makes no WorkItem, so the record is not active; status is determined
    /// by <paramref name="state"/> and the presence of a completed attempt.
    /// </summary>
    /// <remarks>
    /// When <paramref name="result"/> is non-null, a single completed attempt is added and the
    /// draft ids are rewritten as <c>a1-{draft.Id}</c> to mirror
    /// <see cref="PostgresTriageStore.ApplyResult"/>.
    /// When <paramref name="source"/> is <see cref="TriageSource.Issue"/> the record gets
    /// <c>IssueProviderConfigId = "issue-e2e"</c> and a unique <c>IssueIdentifier</c>.
    /// </remarks>
    public static async Task<Guid> SeedRecordAsync(
        E2EFixture fixture,
        string title,
        TriageState state,
        TriageResult? result = null,
        TriageSource source = TriageSource.Operator,
        string? projectId = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var pid = projectId ?? WellKnownIds.DefaultProjectId;

        // Build the attempts list and draft list
        var attempts = new List<TriageAttempt>();
        var drafts = new List<TriageEditableDraft>();

        if (result is not null)
        {
            // Mirror what PostgresTriageStore.ApplyResult does: prefix each draft id with "a1-"
            foreach (var d in result.Drafts)
            {
                var rewrittenId = $"a1-{d.Id}";
                var rewritten = d with { Id = rewrittenId };
                drafts.Add(new TriageEditableDraft { Current = rewritten, Original = rewritten });
            }

            attempts.Add(new TriageAttempt
            {
                WorkItemId = Guid.NewGuid().ToString(),
                StartedAt = now.AddSeconds(-5),
                CompletedAt = now,
                Result = result,
                Outcome = TriageAttemptOutcome.Completed,
            });
        }

        // Source-specific key fields
        string? issueProviderConfigId = null;
        string? issueIdentifier = null;
        if (source == TriageSource.Issue)
        {
            issueProviderConfigId = "issue-e2e";
            issueIdentifier = $"4001-{id:N}";
        }

        var record = new TriageRecord
        {
            Id = id,
            ProjectId = pid,
            Source = source,
            IssueProviderConfigId = issueProviderConfigId,
            IssueIdentifier = issueIdentifier,
            Title = title,
            Request = new TriageRequest
            {
                Title = title,
                WhatHappened = "Observed unexpected behaviour.",
                Expected = "Expected normal behaviour.",
            },
            Attempts = attempts,
            Drafts = drafts,
            State = state,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var entity = TriageEntityMapper.ToEntity(record);

        await using var db = await fixture.DbContextFactory.CreateDbContextAsync();
        db.Triages.Add(entity);
        await db.SaveChangesAsync();

        return id;
    }
}
