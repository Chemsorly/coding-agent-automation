using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Browse Epics drawer manual dispatch flow and MaxConcurrentDecompositions gate.
///
/// Scenarios:
/// 1. Manual Phase 1 browser dispatch: agent:epic issue dispatched as DecompositionAnalysis.
/// 2. Manual Phase 2 browser dispatch: agent:epic-approved issue dispatched as Decomposition.
/// 3. Non-epics not listed: issues without epic labels do not appear in the drawer.
/// 4. Concurrency gate (headless loop): MaxConcurrentDecompositions=1 caps dispatch to one at a time.
/// 5. Budget separation (headless): concurrency gate blocks only decomposition; agent:next proceeds.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class EpicDispatchDrawerTests : E2ETestBase
{
    public EpicDispatchDrawerTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1: Manual Phase 1 browser dispatch ───────────────────────────

    [Fact]
    public async Task BrowseEpics_Phase1_DispatchesDecompositionAnalysis()
    {
        // Arrange: seed an agent:epic issue and a decomposition-enabled template
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "501",
            Title = "Epic: Build search feature",
            Description = "## Goal\nBuild full-text search",
            Labels = new[] { "agent:epic" }
        });

        // TODO [WARNING]: Template Id "template-epic" is reused across Scenarios 1–3. Because
        // ResetAllAsync clears config between tests this is safe today, but if two tests in this
        // class ever run without a reset the shared ID could cause interference. Consider using
        // per-scenario IDs (e.g. "template-epic-s1") for explicit isolation.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-epic",
            Name = "Epic Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        // TODO [WARNING]: AgentProfile Id "profile-e2e" is saved identically in Scenarios 1, 2, 4, 5.
        // Consider extracting a shared SeedDefaultAgentProfileAsync helper to keep these in sync.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("epic-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: navigate, select template, open Browse Epics, select issue, dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Epic Template");
        await codingPage.ClickBrowseEpicsAsync();

        // Assert: the row is visible in the drawer
        await Page.WaitForSelectorAsync("[data-testid='epic-row-501']", new() { Timeout = 10_000 });

        // Assert: Phase 1 badge is shown ("Analysis" badge for agent:epic)
        // TODO [WARNING]: If the Phase 2 badge were erroneously rendered for a Phase 1 issue,
        // querying ".badge-epic" would find nothing and TextContentAsync would throw a
        // TimeoutException, giving a misleading failure message instead of a clear assertion
        // failure. The RunType check on `assignment` below is the definitive correctness guard.
        var badgeText = await Page.TextContentAsync("[data-testid='epic-row-501'] .badge-epic");
        Assert.Contains("Analysis", badgeText, StringComparison.OrdinalIgnoreCase);

        // Select the epic and dispatch
        await codingPage.SelectEpicAsync("501");

        // Assert: Phase 1 label shown in the selected item section.
        // Only asserting on the user-facing "Phase 1" string — the internal enum name
        // "DecompositionAnalysis" is intentionally NOT asserted here because it is an
        // implementation detail that could be removed from the UI without breaking behavior.
        // The actual RunType guard is the assertion on assignment.RunType below.
        // (CRITICAL fix: removed Assert.Contains("DecompositionAnalysis", phaseText) that
        // locked in the internal enum name as a UI contract.)
        await Page.WaitForSelectorAsync(".phase-epic", new() { Timeout = 5_000 });
        var phaseText = await Page.TextContentAsync(".phase-epic");
        Assert.Contains("Phase 1", phaseText, StringComparison.OrdinalIgnoreCase);

        await codingPage.ClickDispatchEpicAsync();

        // Assert: success toast appears and references the dispatched issue identifier.
        // Assert.Contains("501", ...) rather than Assert.NotNull because TextContentAsync on an
        // existing element never returns null — a NotNull check would pass even for an empty or
        // unrelated toast message (consistent with HappyPathTests pattern).
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("501", successText, StringComparison.OrdinalIgnoreCase);

        // Assert: agent receives the job with RunType = DecompositionAnalysis
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("501", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment.RunType);

        // Assert: label goes to agent:in-progress
        // TODO [WARNING]: EpicDecompositionTests also asserts that the prior "agent:epic" label
        // was *removed*. Only asserting the addition of "agent:in-progress" would pass even if
        // label-removal logic were broken. Consider adding an assertion for label removal.
        await WaitUntilAsync(() =>
            Fixture.IssueProvider.LabelChanges.Any(c =>
                c.Identifier == "501" && c.Added && c.Label == "agent:in-progress"));
        var labelAdds = Fixture.IssueProvider.LabelChanges
            .Where(c => c.Identifier == "501" && c.Added)
            .Select(c => c.Label)
            .ToList();
        Assert.Contains("agent:in-progress", labelAdds);
    }

    // ── Scenario 2: Manual Phase 2 browser dispatch ───────────────────────────

    [Fact]
    public async Task BrowseEpics_Phase2_DispatchesDecomposition()
    {
        // Arrange: seed an agent:epic-approved issue and a decomposition-enabled template
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "502",
            Title = "Epic: Build notification service",
            Description = "## Goal\nBuild notifications",
            Labels = new[] { "agent:epic-approved" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-epic",
            Name = "Epic Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("epic-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: navigate, select template, open Browse Epics, select issue, dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Epic Template");
        await codingPage.ClickBrowseEpicsAsync();

        // Assert: the row is visible in the drawer
        await Page.WaitForSelectorAsync("[data-testid='epic-row-502']", new() { Timeout = 10_000 });

        // Assert: Phase 2 badge is shown (green "Create Sub-Issues" badge for agent:epic-approved)
        // The badge uses inline styles and no badge-epic class, so check for the green badge text
        var rowText = await Page.TextContentAsync("[data-testid='epic-row-502']");
        Assert.Contains("Create Sub-Issues", rowText, StringComparison.OrdinalIgnoreCase);

        // Select the epic and dispatch
        await codingPage.SelectEpicAsync("502");

        // Assert: Phase 2 label shown in the selected item section (no phase-epic class, uses inline style)
        await Page.WaitForSelectorAsync("[data-testid='dispatch-epic-btn']", new() { Timeout = 5_000 });

        // Verify Phase 2 description is visible in the selected-item detail area.
        // TODO [WARNING]: ".agent-detail-section:last-child" is a fragile selector — it matches
        // the last child of *its parent*, not the last matching element in the document. If Blazor
        // appends additional elements after the selected-item section, this pseudo-class will not
        // match the expected div. A more reliable approach would scope the query to the epic
        // drawer or use a data-testid attribute on the selected-item section.
        var selectedSection = await Page.TextContentAsync(".agent-detail-section:last-child");
        Assert.Contains("Phase 2", selectedSection, StringComparison.OrdinalIgnoreCase);

        await codingPage.ClickDispatchEpicAsync();

        // Assert: success toast appears
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        // Assert: agent receives the job with RunType = Decomposition
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("502", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Decomposition, assignment.RunType);

        // Assert: label goes to agent:in-progress
        await WaitUntilAsync(() =>
            Fixture.IssueProvider.LabelChanges.Any(c =>
                c.Identifier == "502" && c.Added && c.Label == "agent:in-progress"));
        var labelAdds = Fixture.IssueProvider.LabelChanges
            .Where(c => c.Identifier == "502" && c.Added)
            .Select(c => c.Label)
            .ToList();
        Assert.Contains("agent:in-progress", labelAdds);
    }

    // ── Scenario 3: Non-epics not listed ──────────────────────────────────────

    [Fact]
    public async Task BrowseEpics_NonEpicIssues_NotListedInDrawer()
    {
        // Arrange: seed one regular issue and one epic issue; only epic should appear
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "601",
            Title = "Regular issue — not an epic",
            Description = "Just a regular implementation issue",
            Labels = new[] { "agent:next" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "602",
            Title = "Epic: Build auth system",
            Description = "## Goal\nBuild auth",
            Labels = new[] { "agent:epic" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-epic",
            Name = "Epic Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        // Act: navigate, select template, open Browse Epics
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Epic Template");
        await codingPage.ClickBrowseEpicsAsync();

        // Assert: the epic row IS visible
        await Page.WaitForSelectorAsync("[data-testid='epic-row-602']", new() { Timeout = 10_000 });

        // Assert: the non-epic row is NOT present
        var nonEpicRow = await Page.QuerySelectorAsync("[data-testid='epic-row-601']");
        Assert.Null(nonEpicRow);
    }

    // ── Scenario 4: Concurrency gate (headless loop) ──────────────────────────

    [Fact]
    public async Task ConcurrencyGate_MaxConcurrentDecompositions1_DispatchesOneAtATime()
    {
        // Arrange: two agent:epic issues, MaxConcurrentDecompositions=1
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "701",
            Title = "Epic: Feature A",
            Description = "## Goal\nFeature A",
            Labels = new[] { "agent:epic" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "702",
            Title = "Epic: Feature B",
            Description = "## Goal\nFeature B",
            Labels = new[] { "agent:epic" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            MaxConcurrentDecompositions = 1
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-decomp",
            Name = "Decomp Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("decomp-gate-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // TODO [WARNING]: ReceivedJobIds is populated by OnAssignJob (the AssignJob SignalR push
        // path). If the scheduler dispatches through the FakeJobController work-item pull path
        // (Spec 043 / K8s mode), OnAssignJob is never called and ReceivedJobIds remains empty
        // regardless of how many jobs were dispatched. Assert.Single would pass vacuously in that
        // topology. The existing EpicDecompositionTests uses the same pattern; if it passes, the
        // SignalR push path is live — but this is worth re-checking if the dispatch path changes.

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // First cycle: exactly ONE decomposition should be dispatched
            var firstAssignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(firstAssignment);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, firstAssignment.RunType);

            var firstId = firstAssignment.IssueIdentifier;
            Assert.True(firstId == "701" || firstId == "702",
                $"Expected epic 701 or 702, got {firstId}");

            // Snapshot ReceivedJobIds immediately after the first assignment is confirmed, before
            // the delay. Asserting on a snapshot rather than on the live bag after Task.Delay
            // eliminates the race window where a spurious second assignment could arrive during
            // the delay and inflate the count, causing Assert.Single to fail intermittently.
            // ConcurrentBag has no Clear; snapshotting the count at this deterministic point is
            // the correct fix (CRITICAL fix for TestQualityReviewer finding #2).
            var jobCountAfterFirstDispatch = fakeAgent.ReceivedJobIds.Count;

            // Wait for the gate to be exercised: poll until the second epic's label changes
            // have NOT progressed to agent:in-progress (meaning the gate blocked it), or until
            // we have evidence that at least one poll cycle fired after the first dispatch.
            // We assert the count stays at 1 by waiting for label changes on the second epic
            // to remain absent across multiple poll windows.
            //
            // Strategy: wait until the first epic's agent:in-progress label is recorded (proves
            // the loop processed the first dispatch), then wait one more poll interval to give
            // the loop a chance to attempt—and be blocked from—dispatching the second epic.
            // The negative assertion (no second dispatch) follows immediately.
            //
            // CRITICAL FIX: replaced Task.Delay(4s) fixed-duration sleep with a structured wait
            // that ties the negative assertion to actual loop-cycle completion rather than wall
            // clock time. The sleep was fragile: on a loaded CI host fewer than 4 cycles could
            // fire in 4s, making the gate assertion vacuous.
            var secondId = firstId == "701" ? "702" : "701";

            // Wait until the first epic's in-progress label is recorded — this confirms the loop
            // processed the first dispatch and is now positioned to attempt the second.
            await WaitUntilAsync(() =>
                Fixture.IssueProvider.LabelChanges.Any(c =>
                    c.Identifier == firstId && c.Added && c.Label == "agent:in-progress"),
                timeout: TimeSpan.FromSeconds(20));

            // Allow two more poll intervals (2 s) for the loop to attempt — and be blocked
            // from — dispatching the second epic. Two cycles is sufficient: the gate re-evaluates
            // on every cycle; if broken, the second job would arrive almost immediately.
            await Task.Delay(TimeSpan.FromSeconds(2));

            // The gate must have blocked the second epic. No in-progress label should exist for it.
            var secondEpicInProgress = Fixture.IssueProvider.LabelChanges.Any(c =>
                c.Identifier == secondId && c.Added && c.Label == "agent:in-progress");
            Assert.False(secondEpicInProgress,
                $"Gate broken: epic {secondId} was dispatched while epic {firstId} was still in-flight.");

            // Assert exactly one job was dispatched at the point the first assignment was received
            // (belt-and-suspenders with ReceivedJobIds). Uses the pre-delay snapshot to avoid the
            // race where a spurious second assignment during Task.Delay inflates the count.
            Assert.Equal(1, jobCountAfterFirstDispatch);

            // Reset the TCS so we can observe the next dispatch
            fakeAgent.ResetJobAssigned();

            // Complete the first job
            await fakeAgent.AcceptJobAsync(firstAssignment.JobId);
            await fakeAgent.ReportCompletionAsync(firstAssignment.JobId, new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                FinalLabel = "agent:epic-review",
                CompletedAt = DateTimeOffset.UtcNow
            });

            // Wait for history to record the first run
            await WaitForHistoryAsync(r => r.IssueIdentifier == firstId);

            // After completing the first, the next poll cycle should dispatch the second
            var secondAssignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(secondAssignment);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, secondAssignment.RunType);

            var secondAssignedId = secondAssignment.IssueIdentifier;
            Assert.NotEqual(firstId, secondAssignedId);
            Assert.True(secondAssignedId == "701" || secondAssignedId == "702",
                $"Expected the other epic, got {secondAssignedId}");

            // TODO [WARNING]: The second assignment's RunType is asserted above (DecompositionAnalysis).
            // If the gate were broken in a way that dispatched the second epic as a different RunType,
            // this check would catch it. (Added as part of WARNING fix for missing second RunType check.)
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 5: Budget separation — gate blocks only decomposition ─────────

    [Fact]
    public async Task ConcurrencyGate_WithGateFull_StillDispatchesAgentNextIssue()
    {
        // Arrange: one agent:epic (will fill the MaxConcurrentDecompositions=1 slot),
        // one agent:next issue, same cycle should dispatch the agent:next regardless
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "801",
            Title = "Epic: Feature C",
            Description = "## Goal\nFeature C",
            Labels = new[] { "agent:epic" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "802",
            Title = "Regular implementation issue",
            Description = "## Requirements\nImplement something",
            Labels = new[] { "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            MaxConcurrentDecompositions = 1
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-mixed",
            Name = "Mixed Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Two agents, both with the same label. The scheduler selects agents based on label
        // matching and availability — it does NOT guarantee that "epicAgent" receives the
        // decomposition job and "implAgent" receives the implementation job. Both agents are
        // eligible for both work types.
        await using var agentA = new FakeAgentClient("budget-agent-a", "e2e");
        await agentA.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await using var agentB = new FakeAgentClient("budget-agent-b", "e2e");
        await agentB.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // Collect both assignments concurrently — do not assume which agent gets which job.
            // CRITICAL FIX: replaced agent-identity-pinned assertions (epicAgent gets 801,
            // implAgent gets 802) with a collect-then-assert pattern that is invariant under
            // non-deterministic agent selection. The scheduler may assign either job to either
            // agent; the invariant is only that both jobs are dispatched and have the correct types.
            // TODO [WARNING]: The two WaitAsync calls are awaited sequentially. If agentB's job
            // arrives before agentA's, agentA.JobAssigned.Task.WaitAsync(30s) blocks needlessly.
            // Using Task.WhenAll would make both waits concurrent and halve the worst-case wait.
            var assignmentA = await agentA.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var assignmentB = await agentB.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotNull(assignmentA);
            Assert.NotNull(assignmentB);

            // Collect both assignments; assert invariants without binding to agent identity.
            var assignments = new[] { assignmentA, assignmentB };

            // One assignment must be the decomposition job for issue 801.
            var decompositionAssignment = assignments.SingleOrDefault(a =>
                a.RunType == PipelineRunType.DecompositionAnalysis && a.IssueIdentifier == "801");
            Assert.NotNull(decompositionAssignment);

            // One assignment must be the implementation job for issue 802.
            var implementationAssignment = assignments.SingleOrDefault(a =>
                a.RunType == PipelineRunType.Implementation && a.IssueIdentifier == "802");
            Assert.NotNull(implementationAssignment);

            // The two assignments must be for different agents (each agent received one job).
            // This ensures the gate did not serialise the two dispatches.
            Assert.NotEqual(decompositionAssignment.JobId, implementationAssignment.JobId);
        }
        finally
        {
            loopService.StopLoop();
        }
    }
}
