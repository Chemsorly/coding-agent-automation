using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// A label triage (<c>agent:triage</c> on a tracker issue) on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>,
/// in-process against the E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// <c>TriageInvestigationStep</c> and <c>ReportTriageResultStep</c> only run in the real worker. <c>TriageE2ETests</c>
/// drives an operator triage through a <see cref="FakeAgentClient"/>, which never posts to a tracker. A tracker can be
/// public, so these tests also cover the masking of the project's secrets and of the MCP servers' credentials in the
/// posted comment and in the stored triage.
/// </para>
///
/// <para>
/// Every test uses its own issue number: the harness runs all workers under one workspace root, and the comment and
/// label assertions are per issue.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LabelTriageRealWorkerTests : HeadlessE2ETestBase
{
    private const string ProjectSecret = "project-secret-7781";
    private const string McpSecret = "mcp-secret-9931";

    public LabelTriageRealWorkerTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a triage work item for <paramref name="issueId"/> and marks it Dispatched to <paramref name="agentId"/> in
    /// the database, as the dispatch path would. Returns the work item GUID.
    ///
    /// <para>
    /// A copy of <c>RealWorkerDecompositionTests.CreateAndClaimWorkItemAsync</c> for a triage.
    /// <c>RealAgentWorkerSmokeTests.CreateAndClaimWorkItemAsync</c> maps only Decomposition and Implementation to a run
    /// type. The payload carries the project (the enricher refuses a triage without a project's repository list) and
    /// the run id (the claim records the attempt only for a payload with one); the run id is the work item id, as the
    /// hub matches the attempt by it.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimTriageWorkItemAsync(string issueId, string agentId)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "triage-e2e");

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = WorkItemTaskType.Triage;
        entity.Payload = JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = InitiatedByConstants.LoopTriage,
            TaskType = WorkItemTaskType.Triage,
            RunType = PipelineRunType.Triage,
            AgentSelector = "triage-e2e",
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            RunId = workItemId.ToString(),
            IssueDetail = new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        }, PipelineJsonOptions.Default);
        await db.SaveChangesAsync();

        // Inserting the row routes the selector to a profile of its own (the global routing table is replaced), which
        // has no MCP servers. Route to the triage profile again, before the route is pinned.
        await Fixture.SeedRoutedProfileAsync(LabelTriageProfile());

        // Spec 056: POST /dispatch routes the item and pins its route; the claim refuses an item dispatched without one.
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, CancellationToken.None);
        route.Status.Should().Be(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed, "the work item must carry a pinned route to be claimed");

        // Marked Dispatched to the real agent, as the dispatch path would.
        await using var dispatchDb = Fixture.DbContextFactory.CreateDbContext();
        var dispatched = await dispatchDb.WorkItems.FindAsync([workItemId], CancellationToken.None)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
        dispatched.Status = WorkItemStatus.Dispatched;
        dispatched.AssignedAgentId = agentId;
        dispatched.DispatchedAt = DateTimeOffset.UtcNow;
        await dispatchDb.SaveChangesAsync();

        return workItemId;
    }

    /// <summary>
    /// The profile the triage runs under: an MCP server whose Authorization header is a credential the triage must
    /// never show. The job template is the selector of the work item.
    /// </summary>
    private static AgentProfile LabelTriageProfile() => new()
    {
        Id = "profile-label-triage",
        DisplayName = "Label triage profile",
        JobTemplate = E2ERouting.Key(["triage-e2e"]),
        McpServers =
        [
            new McpServerConfig
            {
                Name = "grafana",
                Type = "http",
                Url = "https://grafana.example.test/mcp",
                Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {McpSecret}" }
            }
        ]
    };

    /// <summary>
    /// Seeds what a tracker triage needs: the enabled template (the project's repository list), the Default project with
    /// a secret, and the routed profile with the MCP server. <c>ResetAllAsync</c> clears all of it between tests.
    /// </summary>
    private async Task SeedAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-label-triage",
            Name = "checkout-api",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            TriageEnabled = true
        }, CancellationToken.None);

        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None)
            ?? throw new InvalidOperationException("The Default project is not seeded");
        await Fixture.ConfigStore.SaveProjectAsync(
            project with { Secrets = new Dictionary<string, string> { ["API_TOKEN"] = ProjectSecret } },
            CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(LabelTriageProfile());
    }

    /// <summary>
    /// The agent's result file: a cause found, one check whose result carries the MCP credential, and one draft whose
    /// body carries the project secret. The parser reads the keys case-insensitively.
    /// </summary>
    private static string ResultJson(string summary) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["verdict"] = "cause_found",
        ["confidence"] = "high",
        ["summary"] = summary,
        ["investigated"] = new[]
        {
            new Dictionary<string, string>
            {
                ["check"] = "Read the pool metrics",
                ["where"] = "grafana",
                ["result"] = $"The query ran with {McpSecret} and showed no free connections."
            }
        },
        ["drafts"] = new[]
        {
            new Dictionary<string, string>
            {
                ["kind"] = "root_fix",
                ["targetRepository"] = "checkout-api",
                ["title"] = "Fix the pool",
                ["body"] = $"Rotate {ProjectSecret} and fix the pool."
            }
        }
    });

    private void AddIssue(string issueId, string title) =>
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = title,
            Description = "",
            Labels = [AgentLabels.Triage]
        });

    /// <summary>
    /// Claims a triage work item for <paramref name="issueId"/>, runs the real worker on it and waits for the worker to
    /// finish. Returns the work item GUID. Every agent call of the run must have been scripted.
    /// </summary>
    private async Task<Guid> RunWorkerAsync(string issueId)
    {
        var agentId = $"label-triage-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimTriageWorkItemAsync(issueId, agentId);

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty(
            "every agent call in the run must be scripted; unscripted prompts: {0}",
            string.Join(" | ", Fixture.AgentProvider.UnscriptedCalls.Select(c => c.Request.Prompt.Split('\n')[0])));

        return workItemId;
    }

    /// <summary>
    /// Reads from the issue provider fake. The API changes its lists from another thread, so a read that overlaps a
    /// write throws "Collection was modified". Each write is a single step, so a plain re-read succeeds.
    /// </summary>
    private static T ReadFake<T>(Func<T> read)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return read();
            }
            catch (InvalidOperationException) when (attempt < 5)
            {
                // Read again.
            }
        }
    }

    private async Task<List<string>> LabelsAsync(string issueId)
    {
        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        return ReadFake(() => issue.Labels.ToList());
    }

    /// <summary>
    /// Waits until <paramref name="issueId"/> carries <c>agent:triage-review</c> and no longer <c>agent:in-progress</c>.
    /// The label is set by the step and again when the API records the completion, so it can land after the work item
    /// is Succeeded. The assertions that follow report the labels that were found.
    /// </summary>
    private Task WaitForReviewLabelAsync(string issueId) => WaitForFinalLabelAsync(issueId, AgentLabels.TriageReview);

    /// <summary>
    /// Waits until <paramref name="issueId"/> carries <paramref name="finalLabel"/> and no longer <c>agent:in-progress</c>.
    /// A check that a label is absent is only meaningful once the final label has landed. The assertions that follow
    /// report the labels that were found.
    /// </summary>
    private async Task WaitForFinalLabelAsync(string issueId, string finalLabel)
    {
        try
        {
            await WaitUntilAsync(async () =>
            {
                var labels = await LabelsAsync(issueId);
                return labels.Contains(finalLabel) && !labels.Contains(AgentLabels.InProgress);
            }, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The assertions report the labels the issue ended with.
        }
    }

    /// <summary>The bodies of the comments posted on <paramref name="issueId"/> that carry the triage marker.</summary>
    private List<string> PostedTriageComments(string issueId) =>
        ReadFake(() => Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId && c.Body.Contains(TriageConstants.CommentMarker, StringComparison.Ordinal))
            .Select(c => c.Body)
            .ToList());

    private Task<TriageRecord?> GetTriageAsync(string issueId) =>
        Fixture.ApiServices.GetRequiredService<ITriageStore>().GetByIssueAsync("issue-e2e", issueId, CancellationToken.None);

    // ── Tests ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A label triage on the real worker: the analysis is posted on the issue, the issue ends <c>agent:triage-review</c>,
    /// and the triage is recorded for review. The project's secret and the MCP server's credential, both in the agent's
    /// result, are masked in the comment and in the stored triage.
    ///
    /// <para>
    /// The comment is masked twice more on its way out (<c>ReportTriageResultStep</c> and <c>OrchestratorProxy</c>), so
    /// only the stored triage shows a result that was reported unmasked: the hub and the store mask nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenALabelTriageRunsOnTheRealWorker_ShouldPostTheAnalysis_SetTheLabel_AndMaskSecrets()
    {
        const string issueId = "6101";
        await SeedAsync();
        AddIssue(issueId, "Checkout returns 502");

        // The investigation takes the first script and the review the second; the review writes no file, so nothing is refined.
        Fixture.AgentProvider
            .Enqueue(new TriageResultScript { RawJson = ResultJson($"Pool exhausted, token {ProjectSecret} was logged.") })
            .EnqueueCodeGen();
        Fixture.QualityGateValidator.AlwaysPass();

        var workItemId = await RunWorkerAsync(issueId);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        // The comment
        var comments = PostedTriageComments(issueId);
        comments.Should().ContainSingle("the analysis is posted once, with the marker");
        var comment = comments[0];
        comment.Should().Contain("## Triage: root cause analysis");
        comment.Should().Contain("**Verdict:** cause found");
        comment.Should().Contain("Pool exhausted");
        comment.Should().Contain("Fix the pool");
        comment.Should().NotContain(ProjectSecret);
        comment.Should().NotContain(McpSecret);

        // The label
        await WaitForReviewLabelAsync(issueId);
        var labels = await LabelsAsync(issueId);
        labels.Should().Contain(AgentLabels.TriageReview);
        labels.Should().NotContain(AgentLabels.Triage);
        labels.Should().NotContain(AgentLabels.InProgress);
        labels.Should().NotContain(AgentLabels.Done);
        labels.Should().NotContain(AgentLabels.Error);

        // The record
        var triage = await GetTriageAsync(issueId);
        triage.Should().NotBeNull();
        triage!.Source.Should().Be(TriageSource.Issue);
        triage.State.Should().Be(TriageState.NeedsReview);
        triage.Attempts.Should().ContainSingle();
        triage.Attempts[0].WorkItemId.Should().Be(workItemId.ToString());
        triage.Drafts.Should().ContainSingle();
        triage.Drafts[0].Current.Id.Should().Be("a1-d1");

        triage.LatestResult.Should().NotBeNull();
        triage.LatestResult!.Summary.Should().Contain("***");
        triage.LatestResult.Summary.Should().NotContain(ProjectSecret);
        triage.Drafts[0].Current.Body.Should().NotContain(ProjectSecret);
        triage.LatestResult.Investigated[0].Result.Should().NotContain(McpSecret);
    }

    /// <summary>
    /// A second label triage on the same issue finds the comment of the first by its marker and updates it, instead of
    /// posting another. The triage keeps both attempts.
    /// </summary>
    [Fact]
    public async Task WhenALabelTriageRunsAgain_ShouldUpdateTheSameComment()
    {
        const string issueId = "6102";
        await SeedAsync();
        AddIssue(issueId, "Checkout returns 502 again");

        // ── First run ───────────────────────────────────────────────────────────────

        Fixture.AgentProvider
            .Enqueue(new TriageResultScript { RawJson = ResultJson("First analysis") })
            .EnqueueCodeGen();

        var firstWorkItemId = await RunWorkerAsync(issueId);
        await WaitForWorkItemStatusAsync(firstWorkItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await WaitForReviewLabelAsync(issueId);

        // ── Second run, asked for on the tracker again ──────────────────────────────

        await Fixture.IssueProvider.RemoveLabelAsync(issueId, AgentLabels.TriageReview, CancellationToken.None);
        await Fixture.IssueProvider.AddLabelsAsync(issueId, [AgentLabels.Triage], CancellationToken.None);

        Fixture.AgentProvider
            .Enqueue(new TriageResultScript { RawJson = ResultJson("Second analysis") })
            .EnqueueCodeGen();

        var secondWorkItemId = await RunWorkerAsync(issueId);
        await WaitForWorkItemStatusAsync(secondWorkItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await WaitForReviewLabelAsync(issueId);

        // ── Assert ──────────────────────────────────────────────────────────────────

        PostedTriageComments(issueId).Should().ContainSingle("the second run updates the comment instead of posting another");

        var updates = ReadFake(() => Fixture.IssueProvider.UpdatedComments.Where(c => c.Identifier == issueId).ToList());
        updates.Should().ContainSingle();
        updates[0].NewBody.Should().Contain("Second analysis");

        var triage = await GetTriageAsync(issueId);
        triage.Should().NotBeNull();
        triage!.Attempts.Should().HaveCount(2);
        triage.Attempts[0].WorkItemId.Should().Be(firstWorkItemId.ToString());
        triage.Attempts[0].Result.Should().NotBeNull();
        triage.Attempts[0].Result!.Summary.Should().Be("First analysis");

        (await LabelsAsync(issueId)).Should().Contain(AgentLabels.TriageReview);
    }

    /// <summary>
    /// An agent that exits cleanly without writing the result file fails the run with the reason, posts nothing on the
    /// issue, and does not ask for a review.
    /// </summary>
    [Fact]
    public async Task WhenTheTriageResultIsMissing_ShouldFailTheRun_WithoutAReviewLabel()
    {
        const string issueId = "6103";
        await SeedAsync();
        AddIssue(issueId, "Checkout returns 502 without a result");

        // Exits 0 and writes no result file; the run stops before the review.
        Fixture.AgentProvider.EnqueueCodeGen();

        var workItemId = await RunWorkerAsync(issueId);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        item.ErrorMessage.Should().Contain("Agent did not produce a triage result at .agent/triage-result.json");

        // The failure label can land after the status, and an absence checked before it would pass whatever the run does next.
        await WaitForFinalLabelAsync(issueId, AgentLabels.Error);

        PostedTriageComments(issueId).Should().BeEmpty("a run without a result posts no analysis");
        var labels = await LabelsAsync(issueId);
        labels.Should().Contain(AgentLabels.Error);
        labels.Should().NotContain(AgentLabels.InProgress);
        labels.Should().NotContain(AgentLabels.TriageReview);
    }
}
