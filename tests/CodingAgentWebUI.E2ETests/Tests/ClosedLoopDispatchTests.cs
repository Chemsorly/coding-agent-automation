using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests the closed-loop autonomous dispatch path: PipelineLoopService polls for
/// agent:next labeled issues and dispatches them to a connected agent without manual intervention.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ClosedLoopDispatchTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public ClosedLoopDispatchTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Loop_FindsLabeledIssue_DispatchesToAgent()
    {
        // Arrange: seed issue with agent:next label
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "100",
            Title = "Closed-loop test issue",
            Description = "## Requirements\nTest issue for closed-loop dispatch\n\n## Acceptance Criteria\n- [ ] Dispatched automatically",
            Labels = new[] { "agent:next" }
        });

        // Save config with template and short poll interval (default is 60s)
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-loop",
                    Name = "Loop Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        // Agent profile matching the fake agent's labels
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect fake agent
        await using var fakeAgent = new FakeAgentClient("loop-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: resolve PipelineLoopService and start it manually
        var loopService = Fixture.Factory.Services.GetRequiredService<PipelineLoopService>();
        await loopService.StartAsync(CancellationToken.None);
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started, "StartLoopAsync should return true with valid config");

            // Wait for the agent to receive the job assignment
            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(assignment);
            Assert.Equal("100", assignment.IssueIdentifier);

            // Agent completes the job
            await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

            // Verify completion recorded in history
            var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "100");
            Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        }
        finally
        {
            loopService.StopLoop();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await loopService.StopAsync(cts.Token);
        }
    }
}
