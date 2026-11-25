using AwesomeAssertions;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// System-tier tests for crash recovery against real Postgres and Redis. When an agent re-registers
/// without its job and the run hash has expired in Redis, <c>AgentOrphanRecoveryService</c> rebuilds a
/// minimal run from the work item in the database, so later hub calls pass <c>[RequiresActiveJob]</c>.
/// Only unit tests with mocks covered this path before.
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class CrashRecoverySystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    /// <summary>
    /// The agent registers with its job, disconnects, and the run hash expires. The registry keeps the
    /// agent's <c>ActiveJobId</c> across the disconnect, so its next registration (without a job) goes to
    /// crash recovery, which rebuilds the run from the database.
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenRunHashExpiredInRedis_ReregistrationShouldRebuildRunFromDatabase()
    {
        // Arrange
        var api = await StartApiAsync();
        var agentId = $"system-agent-{Guid.NewGuid():N}";
        var issue = $"system-{Guid.NewGuid():N}";
        var id = await SeedRunningWorkItemAsync(api, issue, agentId);
        var runs = api.Services.GetRequiredService<IOrchestratorRunService>();
        var registry = api.Services.GetRequiredService<IAgentRegistryService>();

        // FakeAgentClient keeps a static map by agent id, so first is disposed exactly once and
        // before second exists. It has no await using: that scope would dispose it last.
        var first = new FakeAgentClient(agentId, "e2e");
        await first.ConnectWithActiveJobAsync(
            api.ServerAddress, E2EWebApplicationFactory.TestApiKey, id.ToString(), issue, "repo-e2e");
        runs.GetRun(id.ToString()).Should().NotBeNull("registering with an active job tracks the run in Redis");

        await first.DisposeAsync();

        // The disconnect handler marks the agent Disconnected without awaiting the write. The kept
        // ActiveJobId is what sends the next registration to crash recovery.
        await WaitUntilAsync(
            async () =>
            {
                var entry = await registry.GetByAgentIdAsync(new AgentId(agentId));
                return entry is { Status: AgentStatus.Disconnected } && entry.ActiveJobId == id.ToString();
            },
            TimeSpan.FromSeconds(15),
            "the agent should be Disconnected in the registry with its ActiveJobId kept");

        // Simulate the expiry of the run hash.
        await api.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyDeleteAsync($"run:{id}");
        runs.GetRun(id.ToString()).Should().BeNull("the run hash was deleted from Redis");

        // Act — register again without an active job, as a restarted agent does.
        await using var second = new FakeAgentClient(agentId, "e2e");
        await second.ConnectAsync(api.ServerAddress, E2EWebApplicationFactory.TestApiKey);

        // Assert — the rebuilt run is the proof of recovery; ReportOutputAsync only shows the hub filter passed.
        PipelineRun? rebuilt = null;
        await WaitUntilAsync(
            () =>
            {
                rebuilt = runs.GetRun(id.ToString());
                return Task.FromResult(rebuilt is not null);
            },
            TimeSpan.FromSeconds(10),
            "crash recovery should rebuild the run from the work item in the database");

        rebuilt.Should().NotBeNull();
        rebuilt!.InitiatedBy.Should().Be("recovery");
        rebuilt.IssueIdentifier.Value.Should().Be(issue);

        var report = () => second.ReportOutputAsync(id.ToString(), "after-recovery");
        await report.Should().NotThrowAsync("the hub call is accepted for the agent's kept active job");
    }
}
