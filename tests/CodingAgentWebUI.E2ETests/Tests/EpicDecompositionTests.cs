using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests the Epic Decomposition Pipeline two-phase workflow:
/// Phase 1 (agent:epic → agent:epic-review) and Phase 2 (agent:epic-approved → agent:done).
/// Verifies label state machine transitions and correct RunType dispatch.
/// </summary>
[Trait("Category", "E2E")]
public sealed class EpicDecompositionTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public EpicDecompositionTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Phase1_EpicDispatched_CompletesWithEpicReviewLabel()
    {
        // Arrange: seed epic with agent:epic label
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "100",
            Title = "Epic: Implement feature X",
            Description = "## Goal\nBuild feature X end-to-end",
            Labels = new[] { "agent:epic" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-decomp",
                    Name = "Decomp Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true,
                    DecompositionEnabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("decomp-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        await loopService.StartAsync(CancellationToken.None);
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(assignment);
            Assert.Equal("100", assignment.IssueIdentifier);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment.RunType);

            // Agent completes Phase 1 with epic-review label
            await fakeAgent.AcceptJobAsync(assignment.JobId);
            await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                FinalLabel = "agent:epic-review",
                CompletedAt = DateTimeOffset.UtcNow
            });

            // Wait for history to record the completed run
            var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "100");

            // Assert: label transitions
            var labelAdds = Fixture.IssueProvider.LabelChanges
                .Where(c => c.Identifier == "100" && c.Added)
                .Select(c => c.Label)
                .ToList();
            // TODO: Verify label removal of agent:epic when agent:in-progress is added
            Assert.Contains("agent:in-progress", labelAdds);
            Assert.Contains("agent:epic-review", labelAdds);

            // TODO: Verify that a plan comment was posted to the issue via Fixture.IssueProvider.PostedComments

            // Assert: history
            Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, completedRun.RunType);
        }
        finally
        {
            loopService.StopLoop();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await loopService.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Phase2_ApprovedEpic_CompletesWithDoneLabel()
    {
        // Arrange: seed epic with agent:epic-approved label (Phase 2 candidate)
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "200",
            Title = "Epic: Implement feature Y",
            Description = "## Goal\nBuild feature Y end-to-end",
            Labels = new[] { "agent:epic-approved" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-decomp",
                    Name = "Decomp Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true,
                    DecompositionEnabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("decomp-agent-2", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        await loopService.StartAsync(CancellationToken.None);
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(assignment);
            Assert.Equal("200", assignment.IssueIdentifier);
            Assert.Equal(PipelineRunType.Decomposition, assignment.RunType);

            // Agent completes Phase 2 with done label
            await fakeAgent.AcceptJobAsync(assignment.JobId);

            // Create sub-issues via the hub (simulating real agent behavior)
            await fakeAgent.RequestCreateIssueAsync(assignment.JobId,
                "Sub-issue 1: Implement component A",
                "## Requirements\nImplement component A for feature Y",
                new[] { "agent:next" });
            await fakeAgent.RequestCreateIssueAsync(assignment.JobId,
                "Sub-issue 2: Implement component B",
                "## Requirements\nImplement component B for feature Y",
                new[] { "agent:next" });

            await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                FinalLabel = "agent:done",
                CompletedAt = DateTimeOffset.UtcNow
            });

            // Wait for history to record the completed run
            var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "200");

            // Assert: label transitions
            var labelAdds = Fixture.IssueProvider.LabelChanges
                .Where(c => c.Identifier == "200" && c.Added)
                .Select(c => c.Label)
                .ToList();
            // TODO: Verify label removal of agent:epic-approved when agent:in-progress is added, and agent:in-progress when agent:done is added
            Assert.Contains("agent:in-progress", labelAdds);
            Assert.Contains("agent:done", labelAdds);

            // Assert: sub-issues created
            Assert.Equal(2, Fixture.IssueProvider.CreatedIssues.Count);
            Assert.Equal("Sub-issue 1: Implement component A", Fixture.IssueProvider.CreatedIssues[0].Title);
            Assert.Equal("Sub-issue 2: Implement component B", Fixture.IssueProvider.CreatedIssues[1].Title);

            // Assert: history
            Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
            Assert.Equal(PipelineRunType.Decomposition, completedRun.RunType);
        }
        finally
        {
            loopService.StopLoop();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await loopService.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Phase1_AgentFails_LabelTransitionsToError()
    {
        // Arrange: seed epic with agent:epic label
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "300",
            Title = "Epic: Implement feature Z",
            Description = "## Goal\nBuild feature Z end-to-end",
            Labels = new[] { "agent:epic" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-decomp",
                    Name = "Decomp Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true,
                    DecompositionEnabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("decomp-agent-3", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        await loopService.StartAsync(CancellationToken.None);
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started);

            // Wait for the job assignment — may need to skip stale drain dispatches from previous tests
            JobAssignmentMessage assignment;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (true)
            {
                assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (assignment.IssueIdentifier == "300")
                    break;
                // Stale job from queue drain — reset and wait for the correct one
                fakeAgent.ResetJobAssigned();
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Never received job for issue 300");
            }

            Assert.NotNull(assignment);
            Assert.Equal("300", assignment.IssueIdentifier);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment.RunType);

            // Agent reports failure
            await fakeAgent.AcceptJobAsync(assignment.JobId);
            await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
            {
                FinalStep = PipelineStep.Failed,
                FailureReason = "Agent crashed during analysis",
                CompletedAt = DateTimeOffset.UtcNow
            });

            // Wait for history to record the failed run
            var failedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "300");

            // Assert: label transitions
            var labelAdds = Fixture.IssueProvider.LabelChanges
                .Where(c => c.Identifier == "300" && c.Added)
                .Select(c => c.Label)
                .ToList();
            Assert.Contains("agent:in-progress", labelAdds);
            Assert.Contains("agent:error", labelAdds);

            // Assert: history
            Assert.Equal(PipelineStep.Failed, failedRun.FinalStep);
            Assert.Equal(PipelineRunType.DecompositionAnalysis, failedRun.RunType);
        }
        finally
        {
            loopService.StopLoop();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await loopService.StopAsync(cts.Token);
        }
    }
}
