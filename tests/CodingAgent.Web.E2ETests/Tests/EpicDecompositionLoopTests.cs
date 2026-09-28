using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests for the MaxConcurrentDecompositions gate in the closed-loop dispatcher.
/// Verifies that (a) the gate blocks a second epic while the first is active, and (b) the gate
/// only blocks decomposition — regular implementation dispatch is unaffected.
///
/// These are loop-state tests that don't require Playwright; they extend
/// <see cref="HeadlessE2ETestBase"/> and share the same <see cref="E2EFixture"/> as all other
/// E2E tests via <c>[Collection(E2ECollection.Name)]</c>.
///
/// See also: <see cref="EpicDecompositionTests"/> for Phase 1/2 label-state-machine verification
/// and <see cref="EpicDispatchDrawerTests"/> for browser-driven manual dispatch tests.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class EpicDecompositionLoopTests : HeadlessE2ETestBase
{
    public EpicDecompositionLoopTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task MaxConcurrentDecompositions_Gate_BlocksSecondEpic()
    {
        var ct = CancellationToken.None;

        // Arrange: seed two epics
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "400",
            Title = "Epic A",
            Description = "Epic A for gate test",
            Labels = new[] { "agent:epic" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "401",
            Title = "Epic B",
            Description = "Epic B for gate test",
            Labels = new[] { "agent:epic" }
        });

        // Set concurrency limit to 1 so the second epic is gated behind the first
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(ct);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            MaxConcurrentDecompositions = 1,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1)
        }, ct);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-decomp-gate",
            Name = "Decomp Gate Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, ct);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, ct);

        // Two agents: one per epic. FakeAgentClient.JobAssigned.Task is single-use (TrySetResult),
        // so each agent must be a separate instance with a distinct agentId.
        // TODO [WARNING]: Both agents share the same label ("e2e") so either may receive the first
        // epic dispatch non-deterministically. The test asserts RunType on whatever assignment1 holds
        // and NotEqual on identifiers, which is correct, but fakeAgent1.JobAssigned.Task may never
        // complete if fakeAgent2 wins the first dispatch — causing a spurious 30 s timeout instead
        // of a clear gate-bypass failure. Consider using distinct labels per agent and separate
        // AgentProfile entries (one per agent) to deterministically pin which agent receives which epic.
        await using var fakeAgent1 = new FakeAgentClient("decomp-gate-1", "e2e");
        await fakeAgent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await using var fakeAgent2 = new FakeAgentClient("decomp-gate-2", "e2e");
        await fakeAgent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            // TODO [WARNING]: StartLoopAsync returns false when the loop is already active (e.g. a prior
            // test's StopLoop() has not fully unwound before ResetAllAsync returns). Assert the return
            // value is true to catch stale-loop races that would produce phantom dispatches from
            // leftover fixture state. See EpicDecompositionTests for the established pattern.
            await loopService.StartLoopAsync();

            // First epic is dispatched in cycle 1
            var assignment1 = await fakeAgent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment1.RunType);

            // Wait 3 s (≥ 3 poll cycles at 1 s interval): the gate must be blocking the second epic.
            // Task.Delay + IsCompleted is the standard pattern in this codebase (see AgentDisconnectTests).
            // TODO [WARNING]: This assertion is time-based and unreliable under CI load. If the scheduler
            // runs fewer than two full cycles within the 3 s window (due to startup jitter or CPU
            // contention), the assertion passes vacuously — the second epic was never attempted rather
            // than actively blocked. A stronger synchronisation signal (e.g. waiting until the active-
            // decomposition count reaches 1, then asserting the second task is still incomplete) would
            // make the gate-blocking assertion deterministic. See TestQualityReviewer finding #92.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(fakeAgent2.JobAssigned.Task.IsCompleted,
                "Second epic must not be dispatched while the first decomposition is still active");

            // Complete the first assignment — removes it from the active set
            await fakeAgent1.AcceptJobAsync(assignment1.JobId);
            // TODO [WARNING]: ReportCompletionAsync sends the hub message but does not await the
            // server-side completion handler finishing its state update (decrementing the active-
            // decomposition count). If the next loop cycle fires before the server has processed the
            // completion, the gate check sees the old count and the second epic remains blocked,
            // causing a spurious 30 s WaitAsync timeout. The existing EpicDecompositionTests avoids
            // this by waiting for WaitForHistoryAsync after completion before asserting the follow-on
            // label; consider applying the same guard here.
            await fakeAgent1.ReportCompletionAsync(assignment1.JobId, new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                FinalLabel = "agent:epic-review",
                CompletedAt = DateTimeOffset.UtcNow
            });

            // Gate is now clear — second epic must be dispatched in the next cycle
            var assignment2 = await fakeAgent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment2.RunType);
            Assert.NotEqual(assignment1.IssueIdentifier, assignment2.IssueIdentifier);
        }
        finally
        {
            loopService.StopLoop();
        }
    }

    [Fact]
    public async Task MaxConcurrentDecompositions_GateFull_StillDispatchesRegularIssue()
    {
        var ct = CancellationToken.None;

        // Arrange: seed one epic and one regular agent:next issue
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "500",
            Title = "Epic C",
            Description = "Epic C for budget test",
            Labels = new[] { "agent:epic" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "501",
            Title = "Regular Issue",
            Description = "Regular implementation issue",
            Labels = new[] { "agent:next" }
        });

        // Fill the decomposition gate with limit 1
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(ct);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            MaxConcurrentDecompositions = 1,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1)
        }, ct);

        // Template needs DecompositionEnabled AND ImplementationEnabled (defaults to true, set
        // explicitly here for documentation: without ImplementationEnabled the regular agent:next
        // issue dispatch path is disabled and the test silently times out).
        // TODO [WARNING]: No ReviewerConfig or QualityGateConfig is seeded alongside the template.
        // If DispatchScheduler requires a non-null agent-selector derived from the profile or QGC to
        // build the JobDistributionRequest for the implementation dispatch path, the regular agent:next
        // issue may silently fail to dispatch rather than reaching agentForIssue — producing a 30 s
        // WaitAsync timeout rather than a clear assertion failure on the gate behaviour. If tests start
        // timing out here, seed a matching QualityGateConfig with a valid agent selector.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-decomp-budget",
            Name = "Decomp Budget Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true,
            ImplementationEnabled = true
        }, ct);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, ct);

        // Two agents: one will receive the epic, the other the regular issue.
        await using var agentForEpic = new FakeAgentClient("decomp-budget-1", "e2e");
        await agentForEpic.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await using var agentForIssue = new FakeAgentClient("decomp-budget-2", "e2e");
        await agentForIssue.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            // TODO [WARNING]: StartLoopAsync returns false when the loop is already active (e.g. a prior
            // test's StopLoop() has not fully unwound before ResetAllAsync returns). Assert the return
            // value is true to catch stale-loop races that would produce phantom dispatches from
            // leftover fixture state. See EpicDecompositionTests for the established pattern.
            await loopService.StartLoopAsync();

            // Accept the epic assignment to fill the decomposition gate (do NOT complete it)
            var epicAssignment = await agentForEpic.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PipelineRunType.DecompositionAnalysis, epicAssignment.RunType);
            await agentForEpic.AcceptJobAsync(epicAssignment.JobId);
            // Gate is now full: 1 active decomposition, limit = 1

            // The regular agent:next issue must still be dispatched.
            // DispatchScheduler.Decomposition.cs line 36 only blocks further decomposition;
            // the implementation dispatch path runs independently.
            var regularAssignment = await agentForIssue.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PipelineRunType.Implementation, regularAssignment.RunType);
            Assert.Equal("501", regularAssignment.IssueIdentifier);
        }
        finally
        {
            loopService.StopLoop();
        }
    }
}
