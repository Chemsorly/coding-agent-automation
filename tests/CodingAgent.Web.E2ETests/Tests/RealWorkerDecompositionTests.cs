using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Decomposition on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API
/// host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// Phase 2 (<c>DecompositionStep</c>, <c>SubIssueFileParser</c>, <c>CreateSubIssuesStep</c> and
/// <c>PostDecompositionSummaryStep</c>) otherwise only runs in unit tests, or in
/// <c>EpicDecompositionTests</c> with a fake agent that calls the hub directly. These tests also cover partial and
/// total create failure, project epic routing through <c>RequestCreateIssueForProvider</c> (no other E2E test uses it),
/// the hub's refusal of another tracker, and the failures of Phase 1.
/// </para>
///
/// <para>
/// Every test uses its own epic number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerDecompositionTests : HeadlessE2ETestBase
{
    /// <summary>The project of the repo epics: the one <see cref="SeedRepoEpicAsync"/> saves the template in.</summary>
    private static readonly Guid DefaultProject = Guid.Parse(WellKnownIds.DefaultProjectId);

    public RealWorkerDecompositionTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a decomposition work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. Returns the work item GUID.
    ///
    /// <para>
    /// A copy of <c>RealAgentWorkerSmokeTests.CreateAndClaimWorkItemAsync</c> for decomposition. That helper maps
    /// <see cref="WorkItemTaskType.Decomposition"/> to <see cref="PipelineRunType.DecompositionAnalysis"/> only; Phase 2
    /// needs <see cref="PipelineRunType.Decomposition"/> with the same task type, so the run type is a parameter.
    /// </para>
    ///
    /// <para>
    /// The row and the payload must agree on the project and the tracker. The hub rebuilds the run from the row when the
    /// agent registers (project, tracker), and takes the run type from the payload the agent reports; the enricher
    /// derives the scope from the payload's project.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        PipelineRunType runType,
        Guid projectId,
        string issueProviderConfigId = "issue-e2e")
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e", projectId: projectId);

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = WorkItemTaskType.Decomposition;

        var payload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-decomposition-test",
            TaskType = WorkItemTaskType.Decomposition,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = runType,
            ProjectId = projectId,
            IssueDetail = Fixture.IssueProvider.Issues.FirstOrDefault(i => i.Identifier == issueId)
                ?? new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the "e2e" selector.
        await SeedRoutedProfileAsync();
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);

        await db.SaveChangesAsync();

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

    private Task SeedRoutedProfileAsync() =>
        Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-decomposition",
            DisplayName = "Real decomposition profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

    private void AddEpic(string issueId, string label) =>
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Epic: Implement feature X",
            Description = "## Goal\nBuild feature X end-to-end",
            Labels = [label]
        });

    /// <summary>
    /// Seeds a repo epic: the issue with <paramref name="label"/>, the Default project's template for its tracker, and
    /// a routed profile. Without an enabled template for the epic's tracker the enricher refuses the assignment (503) and
    /// the worker retries until the test times out.
    /// </summary>
    private async Task SeedRepoEpicAsync(string issueId, string label)
    {
        AddEpic(issueId, label);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-decomposition-template",
            Name = "Real Decomposition Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        await SeedRoutedProfileAsync();
    }

    /// <summary>The approved plan Phase 2 looks for: a comment carrying the plan marker.</summary>
    private void SeedPlanComment(string issueId) =>
        Fixture.IssueProvider.SeedComment(issueId, 900, $"{CommentMarkers.DecompositionPlan}\nThe plan.", "agent-bot", DateTime.UtcNow);

    /// <summary>
    /// Claims a decomposition work item for <paramref name="issueId"/>, runs the real worker on it and waits for the
    /// worker to finish. Returns the work item GUID. Every agent call of the run must have been scripted.
    /// </summary>
    private async Task<Guid> RunWorkerAsync(
        string issueId,
        PipelineRunType runType,
        Guid projectId,
        string issueProviderConfigId = "issue-e2e")
    {
        var agentId = $"real-decomposition-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId, runType, projectId, issueProviderConfigId);

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
    /// Waits until the labels of <paramref name="issueId"/> that start with <c>agent:</c> are exactly
    /// <paramref name="expected"/>. The terminal label is set through the hub and again when the API records the
    /// completion, so it can land after the worker has stopped. The final assertion names the labels that were found.
    /// </summary>
    private async Task AssertAgentLabelsAsync(string issueId, params string[] expected)
    {
        async Task<List<string>> AgentLabelsAsync()
        {
            // The API rewrites the fake's issue list from another thread, so a read that overlaps a write throws
            // "Collection was modified". The write is a single indexer set, so a plain re-read succeeds.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
                    return issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    // Read again.
                }
            }
        }

        try
        {
            await WaitUntilAsync(
                async () => (await AgentLabelsAsync()).Order().SequenceEqual(expected.Order()),
                TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The assertion below reports the labels the epic ended with.
        }

        (await AgentLabelsAsync()).Should().BeEquivalentTo(expected, "the agent labels of epic {0}", issueId);
    }

    private List<string> SummaryComments(string issueId) =>
        Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId && c.Body.Contains(CommentMarkers.DecompositionSummary))
            .Select(c => c.Body)
            .ToList();

    /// <summary>
    /// An implementation job, active on the hub for a <see cref="FakeAgentClient"/>. A copy of
    /// <c>AgentIssueOpsHubTests.SetupActiveJobAsync</c>, which is private. The caller disposes the agent.
    /// </summary>
    private async Task<(string JobId, FakeAgentClient FakeAgent)> SetupActiveImplementationJobAsync(string agentId)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = "42",
            Title = "Hub scope test issue",
            Description = "## Requirements\nTest the hub's tracker scope.",
            Labels = [AgentLabels.Next]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-decomposition-hub",
            Name = "Decomposition Hub Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-decomposition-hub",
            DisplayName = "Decomposition Hub Agent Profile",
            JobTemplate = E2ERouting.Key("issue-ops"),
        });

        // Connect the agent BEFORE dispatch so FakeJobController can bootstrap it.
        var fakeAgent = new FakeAgentClient(agentId, "issue-ops");
        try
        {
            await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

            var result = await DispatchIssueAsync("42");
            result.Success.Should().BeTrue("the dispatch must succeed: {0}", result.ErrorMessage);

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return (assignment.JobId, fakeAgent);
        }
        catch
        {
            await fakeAgent.DisposeAsync();
            throw;
        }
    }

    // ── D1: Phase 2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 2 on the real worker: the approved plan is found, the agent's sub-issue files are parsed, the sub-issues
    /// are created in file order (the second names the first as its dependency), the summary is posted and the epic
    /// ends <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenPhase2Runs_ShouldCreateIssuesInOrderAndPostSummaryAndSetDone()
    {
        const string issueId = "7601";
        await SeedRepoEpicAsync(issueId, AgentLabels.EpicApproved);
        SeedPlanComment(issueId);
        Fixture.AgentProvider.EnqueueSubIssues(
            new SubIssueFile("First child", "Body one"),
            new SubIssueFile("Second child", "Body two", Dependencies: ["First child"]));

        var workItemId = await RunWorkerAsync(issueId, PipelineRunType.Decomposition, DefaultProject);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var created = Fixture.IssueProvider.CreatedIssues.ToList();
        created.Select(c => c.Title).Should().Equal("First child", "Second child");
        foreach (var issue in created)
        {
            issue.Labels.Should().Contain(AgentLabels.Next);
            issue.Labels.Should().Contain(AgentLabels.Generated);
        }

        // The fake numbers an issue by the issues it holds: the seeded epic, then the first created one, so "2".
        created[1].Body.Should().Contain("Body two");
        created[1].Body.Should().Contain("Depends on " + "#" + "2");

        var summaries = SummaryComments(issueId);
        summaries.Should().ContainSingle();
        summaries[0].Should().Contain("**Created:** 2/2 sub-issues");
        Fixture.IssueProvider.PostedComments.Count(c => c.Identifier == issueId).Should().Be(1,
            "the summary is the only comment Phase 2 posts on the epic");

        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>Phase 2 without a plan comment on the epic fails before any agent call and creates nothing.</summary>
    [Fact]
    public async Task WhenNoPlanCommentExists_Phase2ShouldFail()
    {
        const string issueId = "7602";
        await SeedRepoEpicAsync(issueId, AgentLabels.EpicApproved);

        var workItemId = await RunWorkerAsync(issueId, PipelineRunType.Decomposition, DefaultProject);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        item.ErrorMessage.Should().Contain("No approved decomposition plan found");

        Fixture.IssueProvider.CreatedIssues.Should().BeEmpty();
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }

    // ── D3: partial and total create failure ────────────────────────────────────────

    /// <summary>
    /// One of two sub-issues cannot be created. The step does not retry the error, the other sub-issue is created,
    /// the summary reports both counts, and a partial result is still <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenOneSubIssueCreateFails_ShouldReportPartialAndSetDone()
    {
        const string issueId = "7603";
        await SeedRepoEpicAsync(issueId, AgentLabels.EpicApproved);
        SeedPlanComment(issueId);
        Fixture.IssueProvider.CreateIssueFailTitles.Add("First child");
        Fixture.AgentProvider.EnqueueSubIssues(
            new SubIssueFile("First child", "Body one"),
            new SubIssueFile("Second child", "Body two"));

        await RunWorkerAsync(issueId, PipelineRunType.Decomposition, DefaultProject);

        Fixture.IssueProvider.CreatedIssues.Select(c => c.Title).Should().Equal("Second child");

        var summaries = SummaryComments(issueId);
        summaries.Should().ContainSingle();
        summaries[0].Should().Contain("**Created:** 1/2 sub-issues");
        summaries[0].Should().Contain("**Failed:** 1/2 sub-issues");

        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// Both sub-issues fail: the summary reports 0 of 2 and the epic ends <c>agent:error</c>. The work item status is not
    /// asserted: the step sets the label and continues, so the executor completes the run (see
    /// <c>PostDecompositionSummaryStep</c> and <c>LocalPipelineExecutor</c>), and whether that is intended is not settled.
    /// </summary>
    [Fact]
    public async Task WhenAllSubIssueCreatesFail_ShouldReportAndSetError()
    {
        const string issueId = "7604";
        await SeedRepoEpicAsync(issueId, AgentLabels.EpicApproved);
        SeedPlanComment(issueId);
        Fixture.IssueProvider.CreateIssueFailTitles.Add("First child");
        Fixture.IssueProvider.CreateIssueFailTitles.Add("Second child");
        Fixture.AgentProvider.EnqueueSubIssues(
            new SubIssueFile("First child", "Body one"),
            new SubIssueFile("Second child", "Body two"));

        await RunWorkerAsync(issueId, PipelineRunType.Decomposition, DefaultProject);

        Fixture.IssueProvider.CreatedIssues.Should().BeEmpty();

        var summaries = SummaryComments(issueId);
        summaries.Should().ContainSingle();
        summaries[0].Should().Contain("**Created:** 0/2 sub-issues");
        summaries[0].Should().Contain("**Failed:** 2/2 sub-issues");

        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }

    // ── D4: project epic ────────────────────────────────────────────────────────────

    /// <summary>
    /// An epic in the project's epic tracker creates its sub-issues through <c>RequestCreateIssueForProvider</c>: a
    /// <c>targetRepository</c> that names a template goes to that template's tracker, and an unknown name goes to the
    /// executor's tracker. Nothing is created in the epic tracker, and the plan, summary and labels stay on the epic.
    /// </summary>
    [Fact]
    public async Task WhenProjectEpicNamesTemplate_ShouldRouteToThatTrackerAndUnknownNameToExecutor()
    {
        const string issueId = "7605";
        var projectId = Guid.NewGuid();

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = projectId.ToString(),
            Name = "Real decomposition project",
            Enabled = true,
            EpicIssueProviderId = "issue-e2e"
        }, CancellationToken.None);

        // The only template of the project is also the executor: its repository is the run's.
        // TODO [WARNING]: The routing test cannot tell "named template" routing apart from "unknown name falls back to
        // executor". The project has one template, `backend`, on `issue-e2e-2` with `RepoProviderId = "repo-e2e"`, which
        // is also the run's repo, so `ResolveExecutorIssueProviderId` returns `issue-e2e-2` as well. If
        // `ResolveTargetIssueProviderId(targetRepository, ...)` were broken and always returned null, "Routed child"
        // would fall back to the executor and still land in `SecondaryIssueProvider`. Only the executor-fallback
        // mutation is caught. A second template whose tracker differs from the executor's would separate the two paths.
        await Fixture.ConfigStore.SaveTemplateAsync(projectId.ToString(), new PipelineJobTemplate
        {
            Id = "backend-template",
            Name = "backend",
            IssueProviderId = FakeProviderFactory.SecondaryIssueProviderConfigId,
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        AddEpic(issueId, AgentLabels.EpicApproved);
        SeedPlanComment(issueId);
        await SeedRoutedProfileAsync();
        Fixture.AgentProvider.EnqueueSubIssues(
            new SubIssueFile("Routed child", "Body", TargetRepository: "backend"),
            new SubIssueFile("Fallback child", "Body", TargetRepository: "no-such-repo"));

        await RunWorkerAsync(issueId, PipelineRunType.Decomposition, projectId, issueProviderConfigId: "issue-e2e");

        Fixture.FakeProviders.SecondaryIssueProvider.CreatedIssues.Select(c => c.Title)
            .Should().Equal("Routed child", "Fallback child");
        Fixture.IssueProvider.CreatedIssues.Should().BeEmpty("nothing is created in the epic tracker");

        SummaryComments(issueId).Should().ContainSingle();

        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// An implementation job asks the hub to create an issue in another tracker. Only a decomposition run bound to its
    /// project's epic tracker may, so the hub refuses and the other tracker gets nothing.
    /// </summary>
    [Fact]
    public async Task WhenRunIsNotAProjectEpic_HubShouldRefuseAnotherTracker()
    {
        var (jobId, fakeAgent) = await SetupActiveImplementationJobAsync("agent-decomposition-hub");
        await using (fakeAgent)
        {
            var act = () => fakeAgent.RequestCreateIssueForProviderAsync(
                jobId, FakeProviderFactory.SecondaryIssueProviderConfigId, "t", "b", new[] { AgentLabels.Next });

            var ex = await Assert.ThrowsAsync<HubException>(act);

            ex.Message.Should().Contain("is not in the scope");
        }

        Fixture.FakeProviders.SecondaryIssueProvider.CreatedIssues.Should().BeEmpty();
    }

    // ── D5: Phase 1 failures ────────────────────────────────────────────────────────

    /// <summary>The Phase 1 agent exits non-zero: the run fails with <c>ExitCodeFailure</c> and posts no plan.</summary>
    [Fact]
    public async Task WhenPhase1AgentExitsNonZero_ShouldFailWithExitCodeFailure()
    {
        const string issueId = "7606";
        await SeedRepoEpicAsync(issueId, AgentLabels.Epic);
        Fixture.AgentProvider.Enqueue(new DecompositionPlanScript { ExitCode = 1 });

        var workItemId = await RunWorkerAsync(issueId, PipelineRunType.DecompositionAnalysis, DefaultProject);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        item.ErrorMessage.Should().Be("Agent exited with non-zero exit code: 1");
        item.FailureReason.Should().Be(FailureReason.ExitCodeFailure);

        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
        Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId)
            .Should().NotContain(c => c.Body.Contains(CommentMarkers.DecompositionPlan));
    }

    /// <summary>The Phase 1 plan is shorter than the minimum: the run fails before the review and posts no plan.</summary>
    [Fact]
    public async Task WhenPhase1PlanIsTooShort_ShouldFail()
    {
        const string issueId = "7607";
        await SeedRepoEpicAsync(issueId, AgentLabels.Epic);
        Fixture.AgentProvider.EnqueueDecompositionPlan("short");

        var workItemId = await RunWorkerAsync(issueId, PipelineRunType.DecompositionAnalysis, DefaultProject);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        item.ErrorMessage.Should().Be("Decomposition plan file is too short (5 characters, minimum 20)");

        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
        Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId)
            .Should().NotContain(c => c.Body.Contains(CommentMarkers.DecompositionPlan));
    }
}
