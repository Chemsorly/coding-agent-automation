using System.Net;
using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Operator triage end to end, headless (spec 050, Flow B): the API creates the triage and its Pending
/// WorkItem, the job controller hands it to an agent, the agent reports its result through the hub and
/// completes, and a person creates an issue from a draft.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageE2ETests : HeadlessE2ETestBase
{
    private const string AgentLabel = "triage-e2e";

    public TriageE2ETests(E2EFixture fixture) : base(fixture) { }

    private async Task SeedTemplateAndProfileAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-triage",
            Name = "checkout-api",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-triage",
            DisplayName = "Triage profile",
            MatchLabels = [AgentLabel],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true,
        }, CancellationToken.None);
    }

    private static TriageResult CauseFound() => new()
    {
        Verdict = TriageVerdict.CauseFound,
        Confidence = TriageConfidence.High,
        Summary = "The retry policy re-sends a non-idempotent POST and exhausts the database pool.",
        Investigated = [new TriageCheck { Check = "5xx rate", Where = "grafana · Prometheus", Result = "jump at 14:03" }],
        Evidence = [new TriageEvidence { Id = "E1", Claim = "Pool wait p99 29.8 s", Source = "grafana" }],
        Drafts =
        [
            new TriageDraft { Id = "d1", Kind = TriageDraftKind.RootFix, TargetRepository = "checkout-api", Title = "Do not retry the pay POST", Body = "Remove the retry from RetryPolicy.cs." },
        ],
    };

    [Fact]
    public async Task OperatorTriage_ResultIsRecorded_AndADraftBecomesAnIssue()
    {
        await SeedTemplateAndProfileAsync();
        await using var agent = new FakeAgentClient("agent-triage-e2e", AgentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        using var api = Fixture.CreateApiClient();

        // Start: the triage row and its Pending WorkItem in one call
        var created = await api.PostAsJsonAsync("/api/triages", new CreateTriageRequest
        {
            ProjectId = WellKnownIds.DefaultProjectId,
            RequestedBy = "anna",
            Request = new TriageRequest
            {
                Title = "Checkout returns 502 after the deploy",
                WhatHappened = "One in five checkouts fails with 502",
                Expected = "Checkout completes",
            },
        }, PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var triage = (await created.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;

        // The job controller hands the run to the agent with the triage context
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TriageConstants.IssueIdentifierFor(triage.Record.Id), assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Triage, assignment.RunType);
        Assert.StartsWith("# Triage Context", assignment.TriageContextMarkdown);
        Assert.Equal("Checkout returns 502 after the deploy", assignment.IssueDetail?.Title);
        Assert.Contains("One in five checkouts fails with 502", assignment.IssueDetail?.Description);

        // The agent may not create issues during a triage run
        await Assert.ThrowsAsync<HubException>(() => agent.RequestCreateIssueAsync(assignment.JobId, "x", "y", []));

        // The agent reports its result and completes
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Investigating);
        await agent.ReportTriageResultAsync(assignment.JobId, CauseFound());
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.ReportingRca);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var detail = await WaitForAsync(async () =>
        {
            var d = await api.GetFromJsonAsync<TriageDetail>($"/api/triages/{triage.Record.Id}", PipelineJsonOptions.Default);
            return d is { Status: TriageStatus.NeedsReview } ? d : null;
        });
        Assert.Equal(TriageVerdict.CauseFound, detail.Record.LatestResult!.Verdict);
        Assert.Single(detail.Record.Drafts);

        // The run page's link back to the triage
        var byRun = await api.GetAsync($"/api/triages/by-run/{assignment.JobId}");
        Assert.Equal(HttpStatusCode.OK, byRun.StatusCode);

        // A person creates the draft as an issue in the repository's tracker
        var issuesBefore = Fixture.IssueProvider.CreatedIssues.Count;
        var createIssues = await api.PostAsJsonAsync($"/api/triages/{triage.Record.Id}/create-issues", new CreateTriageIssuesRequest
        {
            DraftIds = ["d1"],
            Queue = true,
            CreatedBy = "anna",
        }, PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, createIssues.StatusCode);
        var result = (await createIssues.Content.ReadFromJsonAsync<CreateTriageIssuesResult>(PipelineJsonOptions.Default))!;
        Assert.Single(result.Created);
        Assert.Empty(result.Errors);
        Assert.Equal(issuesBefore + 1, Fixture.IssueProvider.CreatedIssues.Count);
        var issue = Fixture.IssueProvider.CreatedIssues[^1];
        Assert.Equal("Do not retry the pay POST", issue.Title);
        Assert.Contains("Remove the retry from RetryPolicy.cs.", issue.Body);
        Assert.Contains(AgentLabels.Next, issue.Labels!);
        Assert.Contains(AgentLabels.Generated, issue.Labels!);

        var final = await api.GetFromJsonAsync<TriageDetail>($"/api/triages/{triage.Record.Id}", PipelineJsonOptions.Default);
        Assert.Equal(TriageStatus.IssuesCreated, final!.Status);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (await probe() is { } value)
                return value;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The triage did not reach the expected state in 30 s");
            await Task.Delay(200);
        }
    }
}
