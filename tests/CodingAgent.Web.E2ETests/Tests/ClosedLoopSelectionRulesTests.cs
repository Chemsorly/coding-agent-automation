using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests for closed-loop selection rules:
/// skip labels, FIFO ordering, type priority and budget sharing,
/// feature flags, disabled template / project, and blocked dependencies.
///
/// Each test drives the real PipelineLoopService against in-memory fakes and
/// asserts on dispatch outcomes through FakeAgentClient.JobAssigned and
/// FakeJobController.ClaimedWorkItemIds.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ClosedLoopSelectionRulesTests : HeadlessE2ETestBase
{
    public ClosedLoopSelectionRulesTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared setup helpers ───────────────────────────────────────────────────

    private Task SaveDefaultTemplateAsync(
        string id = "template-sel",
        bool implementationEnabled = true,
        bool reviewEnabled = false,
        bool decompositionEnabled = false,
        bool enabled = true,
        string projectId = WellKnownIds.DefaultProjectId) =>
        Fixture.ConfigStore.SaveTemplateAsync(projectId, new PipelineJobTemplate
        {
            Id = id,
            Name = $"Sel-Rules Template ({id})",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = enabled,
            ImplementationEnabled = implementationEnabled,
            ReviewEnabled = reviewEnabled,
            DecompositionEnabled = decompositionEnabled
        }, CancellationToken.None);

    private Task SaveDefaultAgentProfileAsync(string id = "profile-sel") =>
        Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = id,
            DisplayName = "Selection Rules Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

    private async Task SetPollIntervalAsync(int maxRunsPerCycle, TimeSpan? pollInterval = null)
    {
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = pollInterval ?? TimeSpan.FromSeconds(1),
            ClosedLoopMaxRunsPerCycle = maxRunsPerCycle
        }, CancellationToken.None);
    }

    // ── Scenario 1: Skip Labels ────────────────────────────────────────────────

    /// <summary>
    /// Issues with agent:error or agent:needs-refinement alongside agent:next are skipped.
    /// Only the clean issue (agent:next only) is dispatched.
    /// </summary>
    [Fact]
    public async Task SkipLabels_ErrorAndNeedsRefinement_OnlyCleanIssueDispatched()
    {
        // Arrange
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "1",
            Title = "Clean issue",
            Description = "Should be dispatched",
            Labels = new[] { "agent:next" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "2",
            Title = "Error issue",
            Description = "Should be skipped",
            Labels = new[] { "agent:next", "agent:error" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3",
            Title = "Needs refinement issue",
            Description = "Should be skipped",
            Labels = new[] { "agent:next", "agent:needs-refinement" }
        });

        await SaveDefaultTemplateAsync();
        await SaveDefaultAgentProfileAsync();
        // Budget=1: the loop attempts exactly one dispatch per cycle. If skip-label logic is broken
        // and issues "2" or "3" are selected instead of "1", the identifier assertion fails
        // unambiguously. A higher budget would allow the loop to dispatch multiple issues in one
        // cycle, creating a race window where only the first JobAssigned TCS fires and subsequent
        // buggy dispatches can race past StopLoop() undetected.
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-skip-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started, "StartLoopAsync should succeed with valid template");

            // Wait for the one clean dispatch
            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("1", assignment.IssueIdentifier);

            // Stop and verify exactly one dispatch occurred (issues 2 and 3 were skipped).
            // Budget=1 ensures the loop can only attempt one dispatch per cycle, so Assert.Single
            // is always deterministic: if the wrong issue were selected it would still be a single
            // claim, caught by the identifier assertion above.
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            Assert.Single(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 2: FIFO Ordering ──────────────────────────────────────────────

    /// <summary>
    /// Issues are dispatched in oldest-CreatedAt order (FIFO).
    /// Seeds three issues in reverse chronological insertion order to verify
    /// that SortByCreatedAtFifo re-orders them, not insertion order.
    /// </summary>
    [Fact]
    public async Task Fifo_ThreeIssues_OldestDispatchedFirst()
    {
        // Arrange — seed in reverse chronological order (newest first in the list)
        // so that insertion-order-only dispatch would pick ID "30", not ID "10".
        var now = DateTime.UtcNow;
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "30",
            Title = "Newest issue",
            Description = "Created most recently",
            Labels = new[] { "agent:next" },
            CreatedAt = now  // newest — first in list
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "20",
            Title = "Middle issue",
            Description = "Created in the middle",
            Labels = new[] { "agent:next" },
            CreatedAt = now.AddDays(-1)  // middle
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "10",
            Title = "Oldest issue",
            Description = "Created longest ago",
            Labels = new[] { "agent:next" },
            CreatedAt = now.AddDays(-2)  // oldest — last in list
        });

        await SaveDefaultTemplateAsync();
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);  // budget 1 to isolate the first dispatch

        await using var fakeAgent = new FakeAgentClient("loop-fifo-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // FIFO: oldest CreatedAt must be dispatched first (ID "10"), NOT insertion order (ID "30")
            Assert.Equal("10", assignment.IssueIdentifier);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 3: Type Priority and Budget Sharing ───────────────────────────

    /// <summary>
    /// Dispatch priority: PRs first, then decomposition, then implementation issues.
    /// With budget=2, the PR and epic are dispatched; the issue waits.
    /// With budget=3, all three are dispatched.
    /// </summary>
    [Fact]
    public async Task TypePriority_Budget2_PrAndEpicDispatchedIssueWaits()
    {
        // Arrange: seed one item of each type
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 42,
            Identifier = "42",
            Title = "PR for review",
            Description = "PR body",
            Labels = new[] { "agent:next" },
            BranchName = "feature/some-branch",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/42",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow
        });
        // Dispatch preparation calls GetIssueAsync with the PR identifier. On GitHub every PR is
        // also an issue under the same number; the harness models them separately, so we must seed
        // the issue side or orchestration returns null and the review work item is never created.
        // Do NOT include agent:next here — that would add this PR-as-issue to the implementation
        // queue, causing the MinIssueSlots floor pass to re-dispatch it as an implementation item
        // (hitting a 409 conflict since the review work item already exists) and consuming a
        // budget slot without producing a second work item that agents can receive.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "PR for review",
            Description = "PR body",
            Labels = Array.Empty<string>()
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "200",
            Title = "Epic — phase 2",
            Description = "Epic approved for decomposition",
            Labels = new[] { "agent:epic-approved" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "300",
            Title = "Regular issue",
            Description = "Should wait until budget allows",
            Labels = new[] { "agent:next" }
        });

        await SaveDefaultTemplateAsync(
            reviewEnabled: true,
            implementationEnabled: true,
            decompositionEnabled: true);
        await SaveDefaultAgentProfileAsync();
        // Use a large poll interval (60s) so that after cycle 1 exhausts its budget=2, the loop
        // sleeps for 60s before cycle 2. This gives StopLoop() ample time to fire before a second
        // cycle can start, preventing the third work item (implementation) from being claimed in
        // cycle 2. Without this, a 1s interval allows cycle 2 to begin in the ~0-1s window between
        // WaitUntilAsync(Count >= 2) returning and StopLoop() executing, making Assert.Equal(2) flaky.
        await SetPollIntervalAsync(maxRunsPerCycle: 2, pollInterval: TimeSpan.FromSeconds(60));

        // Connect 3 agents so capacity is never the bottleneck
        await using var agent1 = new FakeAgentClient("loop-prio-1", "e2e");
        await using var agent2 = new FakeAgentClient("loop-prio-2", "e2e");
        await using var agent3 = new FakeAgentClient("loop-prio-3", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent3.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // Wait for both budget-2 dispatches using event-driven JobAssigned signals.
            // Polling ClaimedWorkItemIds.Count was unreliable under CI load because it required
            // the full orchestration→HTTP→DB→FakeJobController chain to complete within 30s —
            // two HTTP round-trips to the test API host were slow enough to time out.
            // JobAssigned fires as soon as FakeJobController bootstraps the agent, which is the
            // same signal but observed directly rather than through a shared counter.
            // The 60s poll interval ensures cycle 2 cannot start before StopLoop() fires,
            // making the snapshot deterministically contain only the cycle-1 dispatches.
            await Task.WhenAll(
                agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30)),
                agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            // Snapshot after loop is stopped (no more adds possible from new cycles)
            var claimedIds = Fixture.JobController.ClaimedWorkItemIds.ToList();

            // With budget=2 and a 60s poll interval, exactly 2 work items are claimed in cycle 1.
            Assert.Equal(2, claimedIds.Count);

            // Check task types in the database: should have Review + Decomposition, NOT Implementation
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var items = await db.WorkItems.AsNoTracking()
                .Where(w => claimedIds.Contains(w.Id))
                .ToListAsync();

            Assert.Contains(items, i => i.TaskType == WorkItemTaskType.Review);
            Assert.Contains(items, i => i.TaskType == WorkItemTaskType.Decomposition);
            Assert.DoesNotContain(items, i => i.TaskType == WorkItemTaskType.Implementation);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    [Fact]
    public async Task TypePriority_Budget3_AllThreeTypesDispatched()
    {
        // Arrange: same as Budget2 test but with budget=3
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 43,
            Identifier = "43",
            Title = "PR for review",
            Description = "PR body",
            Labels = new[] { "agent:next" },
            BranchName = "feature/another-branch",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/43",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow
        });
        // Dispatch preparation calls GetIssueAsync with the PR identifier. On GitHub every PR is
        // also an issue under the same number; the harness models them separately, so we must seed
        // the issue side or orchestration returns null and the review work item is never created.
        // Do NOT include agent:next — see Budget2 test for the rationale.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "43",
            Title = "PR for review",
            Description = "PR body",
            Labels = Array.Empty<string>()
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "201",
            Title = "Epic — phase 2",
            Description = "Epic approved for decomposition",
            Labels = new[] { "agent:epic-approved" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "301",
            Title = "Regular issue",
            Description = "Should be dispatched with budget=3",
            Labels = new[] { "agent:next" }
        });

        await SaveDefaultTemplateAsync(
            reviewEnabled: true,
            implementationEnabled: true,
            decompositionEnabled: true);
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 3);

        await using var agent1 = new FakeAgentClient("loop-prio3-1", "e2e");
        await using var agent2 = new FakeAgentClient("loop-prio3-2", "e2e");
        await using var agent3 = new FakeAgentClient("loop-prio3-3", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent3.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // Wait for all 3 dispatches using event-driven JobAssigned signals on all 3 agents.
            // See TypePriority_Budget2 for the rationale: ClaimedWorkItemIds polling was timing
            // out because two HTTP round-trips to the test API were too slow under CI load.
            // MaxRunsPerCycle resets every poll interval, so after all 3 fire we stop immediately.
            await Task.WhenAll(
                agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30)),
                agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30)),
                agent3.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            // Snapshot after loop is stopped
            var claimedIds = Fixture.JobController.ClaimedWorkItemIds.ToList();

            // With budget=3, all three work items should be claimed
            Assert.Equal(3, claimedIds.Count);

            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var items = await db.WorkItems.AsNoTracking()
                .Where(w => claimedIds.Contains(w.Id))
                .ToListAsync();

            Assert.Contains(items, i => i.TaskType == WorkItemTaskType.Review);
            Assert.Contains(items, i => i.TaskType == WorkItemTaskType.Decomposition);
            Assert.Contains(items, i => i.TaskType == WorkItemTaskType.Implementation);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 4: Feature Flags ──────────────────────────────────────────────

    /// <summary>
    /// With ReviewEnabled=false, PRs labelled agent:next are not dispatched even if present.
    /// </summary>
    [Fact]
    public async Task FeatureFlags_ReviewDisabled_PrNotDispatched()
    {
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 50,
            Identifier = "50",
            Title = "PR that should not be dispatched",
            Description = "Review is disabled",
            Labels = new[] { "agent:next" },
            BranchName = "feature/flag-test",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/50",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow
        });

        // ReviewEnabled=false; no implementation/decomposition work seeded
        await SaveDefaultTemplateAsync(reviewEnabled: false, implementationEnabled: false);
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-flag-review-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // Allow two cycles to complete (2s at 1s poll interval)
            // TODO [WARNING]: This is a negative test asserting that nothing was dispatched.
            // Task.Delay(2s) provides only a time-based guarantee that the loop ran at least once,
            // which may not hold under CI load if the first cycle takes >2s to complete.
            // The test could pass vacuously (loop never polled, ClaimedWorkItemIds empty) without
            // exercising the ReviewEnabled=false flag at all. A stronger approach is to verify
            // that the loop is still active and reached at least one iteration before asserting
            // emptiness (e.g. WaitUntilAsync(loopService.CycleCount >= 1)).
            await Task.Delay(TimeSpan.FromSeconds(2));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    /// <summary>
    /// With ImplementationEnabled=false, issues labelled agent:next are not dispatched.
    /// </summary>
    [Fact]
    public async Task FeatureFlags_ImplementationDisabled_IssueNotDispatched()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "51",
            Title = "Issue that should not be dispatched",
            Description = "Implementation is disabled",
            Labels = new[] { "agent:next" }
        });

        await SaveDefaultTemplateAsync(implementationEnabled: false);
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-flag-impl-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // TODO [WARNING]: Same time-based negative-test weakness as FeatureFlags_ReviewDisabled.
            // If the loop never completes a cycle within 2s (e.g. under CI load), ClaimedWorkItemIds
            // is empty regardless of whether ImplementationEnabled=false logic is working, making the
            // assertion vacuously true.
            await Task.Delay(TimeSpan.FromSeconds(2));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    /// <summary>
    /// With DecompositionEnabled=false, epics are not dispatched.
    /// </summary>
    [Fact]
    public async Task FeatureFlags_DecompositionDisabled_EpicNotDispatched()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "52",
            Title = "Epic that should not be dispatched",
            Description = "Decomposition is disabled",
            Labels = new[] { "agent:epic-approved" }
        });

        // TODO [WARNING]: implementationEnabled: false is set here alongside decompositionEnabled: false,
        // which introduces a confounding variable. The feature flag under test is DecompositionEnabled;
        // setting ImplementationEnabled=false masks whether epics are actually blocked by the decomposition
        // flag or by some interaction with implementation being disabled. A cleaner test would only set
        // decompositionEnabled: false and leave implementationEnabled at its default (true).
        await SaveDefaultTemplateAsync(decompositionEnabled: false, implementationEnabled: false);
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-flag-decomp-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // TODO [WARNING]: Same time-based negative-test weakness as FeatureFlags_ReviewDisabled.
            await Task.Delay(TimeSpan.FromSeconds(2));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));

            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 5: Disabled Template and Disabled Project ─────────────────────

    /// <summary>
    /// A disabled template is never polled; no dispatch occurs even with a waiting issue.
    /// </summary>
    [Fact]
    public async Task DisabledTemplate_NothingDispatched()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "60",
            Title = "Issue for disabled template",
            Description = "Template is disabled",
            Labels = new[] { "agent:next" }
        });

        // Template with Enabled=false
        await SaveDefaultTemplateAsync(enabled: false);
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-disabled-tmpl-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();

        // A disabled template means StartLoopAsync will fail with "No enabled templates"
        var started = await loopService.StartLoopAsync();
        // TODO [WARNING]: The early-return path here passes trivially with only Assert.Empty after
        // StartLoopAsync returned false — the loop never ran, so ClaimedWorkItemIds is always empty.
        // This cannot distinguish "loop correctly refused because template is disabled" from "loop
        // failed to start for an unrelated reason (misconfiguration, missing provider registration)".
        // A stronger test would assert that started==false is the expected behaviour for a disabled
        // template, or would also save an enabled template and verify that only the disabled one's
        // issues are not dispatched (so the loop actually runs and the negative assertion is meaningful).
        // If it didn't start (no enabled templates), that's the expected behaviour
        if (!started)
        {
            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));
            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    /// <summary>
    /// A disabled project is skipped entirely; templates in it are never polled.
    /// </summary>
    [Fact]
    public async Task DisabledProject_NothingDispatched()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "61",
            Title = "Issue for disabled project",
            Description = "Project is disabled",
            Labels = new[] { "agent:next" }
        });

        // Create a new disabled project with an enabled template
        var disabledProjectId = "disabled-project-1";
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = disabledProjectId,
            Name = "Disabled Project",
            Enabled = false,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(disabledProjectId, new PipelineJobTemplate
        {
            Id = "template-disabled-proj",
            Name = "Template in Disabled Project",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ImplementationEnabled = true
        }, CancellationToken.None);

        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-disabled-proj-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();

        // StartLoopAsync will fail — disabled project means its template isn't "enabled" from
        // the loop's perspective since FlattenTemplates skips disabled projects
        var started = await loopService.StartLoopAsync();
        // TODO [WARNING]: This test does NOT configure any enabled template in the default project
        // (only a disabled project with an enabled template is saved). If StartLoopAsync requires
        // at least one enabled template in an enabled project to return true, the loop will always
        // take the early-return path here — meaning the test only ever asserts that "loop won't
        // start when there are no enabled templates," which is already covered by
        // DisabledTemplate_NothingDispatched. To actually exercise "project disabled, its template
        // is not polled," the test should also save an enabled template in the default (enabled)
        // project so the loop starts, then verify that only the disabled project's issues are not
        // dispatched.
        if (!started)
        {
            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            loopService.StopLoop();
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(10));
            Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    /// <summary>
    /// Templates from the alphabetically-first project are polled before those from later projects.
    /// With budget=1, the issue belonging to the first-project template is dispatched.
    /// </summary>
    [Fact]
    public async Task EnabledTemplates_PolledInProjectNameOrder()
    {
        // Project "A-Project" (alphabetically first) issues issue "100"
        // Project "Z-Project" (alphabetically last) issues issue "200"
        // Budget=1 → only the first-ordered template dispatches
        const string aProjectId = "a-project-id";
        const string zProjectId = "z-project-id";

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = aProjectId,
            Name = "A-Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = zProjectId,
            Name = "Z-Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(aProjectId, new PipelineJobTemplate
        {
            Id = "template-a-proj",
            Name = "Template A",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ImplementationEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(zProjectId, new PipelineJobTemplate
        {
            Id = "template-z-proj",
            Name = "Template Z",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ImplementationEnabled = true
        }, CancellationToken.None);

        // Both issues have agent:next; the loop's single provider sees both.
        // budget=1 means only one dispatch per cycle.
        // TODO [WARNING]: Issue "100" has CreatedAt=now.AddDays(-1) (older) while "200" has
        // CreatedAt=now (newer). FIFO ordering by CreatedAt would also dispatch "100" first,
        // regardless of project order — both conditions point at the same outcome. A project-ordering
        // bug (Z-Project polled before A-Project) would be masked because FIFO still produces "100".
        // To isolate the project-ordering variable, both issues should have identical (or null) CreatedAt.
        // TODO [WARNING]: The issue docs (docs/projects.md) say templates are polled "in project order"
        // (list order / insertion order), but FlattenTemplates currently sorts by p.Name alphabetically.
        // This test locks in alphabetical-name ordering (A-Project before Z-Project). If FlattenTemplates
        // is changed to respect insertion/list order as the docs describe, this test will fail without
        // any product logic regression. The test should document which rule it is actually testing.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "100",
            Title = "Issue from A-Project",
            Description = "From alphabetically first project",
            Labels = new[] { "agent:next" },
            CreatedAt = DateTime.UtcNow.AddDays(-1)  // older so FIFO doesn't interfere
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "200",
            Title = "Issue from Z-Project",
            Description = "From alphabetically last project",
            Labels = new[] { "agent:next" },
            CreatedAt = DateTime.UtcNow  // newer
        });

        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-order-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // With budget=1, the first template polled (A-Project) dispatches issue "100"
            // The issue from Z-Project ("200") must wait
            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("100", assignment.IssueIdentifier);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    // ── Scenario 6: Blocked Dependency ────────────────────────────────────────

    /// <summary>
    /// An issue whose body declares "Blocked by #100" is held while issue #100 is open.
    /// Once #100 is marked closed, the next cycle dispatches the blocked issue.
    /// </summary>
    [Fact]
    public async Task BlockedDependency_IssueHeldWhileBlockerOpen_DispatchedAfterBlockerCloses()
    {
        // Arrange: seed issue 60 blocked by #100 (open)
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "60",
            Title = "Blocked issue",
            Description = "Blocked by #100",  // same body format as DependencyBlockingTests
            Labels = new[] { "agent:next" }
        });
        // Issue #100 is NOT in ClosedIssueIdentifiers — it is open

        await SaveDefaultTemplateAsync();
        await SaveDefaultAgentProfileAsync();
        await SetPollIntervalAsync(maxRunsPerCycle: 1);

        await using var fakeAgent = new FakeAgentClient("loop-dep-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();

        // Part A: blocker open — issue 60 must NOT be dispatched
        var startedA = await loopService.StartLoopAsync();
        Assert.True(startedA, "StartLoopAsync should succeed");

        try
        {
            // Allow at least two full poll cycles (poll interval = 1s, so 2.5s is enough)
            // TODO [WARNING]: Task.Delay(2.5s) at a 1s poll interval provides only ~2 poll cycles to
            // establish that the blocked issue was NOT dispatched. Under CI load the first cycle may not
            // complete within 2.5s, causing Assert.Empty to pass vacuously (loop never ran a full cycle).
            // A stronger guard would poll WaitUntilAsync for a sentinel condition (e.g. a counter
            // incremented per loop cycle) to confirm at least one cycle completed before asserting emptiness.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            loopService.StopLoop();
            // Use 30s (matching other dispatch-wait timeouts) so CI under load has time for the
            // current cycle to finish before CleanupAsync sets IsLoopActive=false. 10s was not
            // enough when the suite ran near its 228-test end under maximum resource pressure.
            await WaitUntilAsync(() => !loopService.IsLoopActive, timeout: TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Ensure the loop is stopped even if WaitUntilAsync times out, so the test fixture
            // isn't left in a broken state for subsequent tests in the collection.
            loopService.StopLoop();
            throw;
        }

        Assert.Empty(Fixture.JobController.ClaimedWorkItemIds);

        // Part B: mark blocker closed and run a second cycle
        Fixture.IssueProvider.ClosedIssueIdentifiers.Add("100");

        // Reset the one-shot TaskCompletionSource before starting the second cycle.
        // JobAssigned is a one-shot TCS: if Part A had dispatched issue #60 (a bug), TrySetResult
        // would have completed it, and Part B's WaitAsync would return the stale Part-A result
        // immediately — masking the very bug this test is designed to catch. Resetting here ensures
        // the await below can only be satisfied by a dispatch that occurs in Part B.
        fakeAgent.ResetJobAssigned();

        var startedB = await loopService.StartLoopAsync();
        Assert.True(startedB, "Second StartLoopAsync should succeed after StopLoop");

        try
        {
            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("60", assignment.IssueIdentifier);
        }
        finally
        {
            loopService.StopLoop();
        }
    }
}
