using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// System-tier tests that verify the API behaves correctly when Postgres becomes unreachable
/// and then recovers. These tests require real Docker containers (via <see cref="SystemContainers"/>)
/// and exercise the <c>/readyz</c> probe, the SignalR hub's orphan-recovery path, and the
/// work-item status endpoint under a real Postgres outage.
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class PostgresFailureSystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    [RequiresDockerFact]
    public async Task WhenPostgresStops_ShouldReportUnreachableAndRecoverOnRestart()
    {
        var issue = $"system-{Guid.NewGuid():N}";
        var api = await StartApiAsync();

        // Wait for /readyz to be ready on startup
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should reach 200 ready before Postgres is stopped");

        // Stop Postgres
        await Containers.StopPostgresAsync();

        // Assert /readyz reaches 503 with database_unreachable
        await WaitUntilAsync(
            async () =>
            {
                var (status, statusField, reason) = await GetProbeAsync(api, "/readyz");
                return status == HttpStatusCode.ServiceUnavailable
                    && statusField == "unhealthy"
                    && reason == "database_unreachable";
            },
            TimeSpan.FromSeconds(60),
            "/readyz should report database_unreachable after Postgres is stopped");

        var (readyzStatus, readyzStatusField, readyzReason) = await GetProbeAsync(api, "/readyz");
        readyzStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
        readyzStatusField.Should().Be("unhealthy");
        readyzReason.Should().Be("database_unreachable");

        // Assert /healthz remains 200 — liveness does not check the database
        var (healthzStatus, healthzField, _) = await GetProbeAsync(api, "/healthz");
        healthzStatus.Should().Be(HttpStatusCode.OK);
        healthzField.Should().Be("ok");

        // Assert GET /api/ui/health reports DatabaseConnected=false while Redis stays up
        using var uiClient = CreateUiClient(api);
        var uiHealth = await uiClient.GetFromJsonAsync<UiHealthResponse>("/api/ui/health");
        uiHealth!.DatabaseConnected.Should().BeFalse();
        uiHealth.RedisConnected.Should().BeTrue();

        // Restart Postgres
        await Containers.StartPostgresAsync();

        // Assert /readyz recovers to 200 after restart
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should recover to 200 after Postgres is restarted");

        var (recoveredStatus, recoveredField, _) = await GetProbeAsync(api, "/readyz");
        recoveredStatus.Should().Be(HttpStatusCode.OK);
        recoveredField.Should().Be("ready");

        // Assert /api/ui/health reports DatabaseConnected=true after recovery
        var uiHealthAfter = await uiClient.GetFromJsonAsync<UiHealthResponse>("/api/ui/health");
        uiHealthAfter!.DatabaseConnected.Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task WhenPostgresIsDown_AgentRegistrationWithActiveJobShouldFailAndSucceedAfterRecovery()
    {
        var issue = $"system-{Guid.NewGuid():N}";
        var agentId = $"system-agent-{Guid.NewGuid():N}";
        var api = await StartApiAsync();

        // Wait for /readyz to be ready
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should be ready before seeding a work item");

        // Seed a Running work item directly in the database
        var id = await SeedRunningWorkItemAsync(api, issue, agentId);

        // Stop Postgres and wait for the probe to report the outage
        await Containers.StopPostgresAsync();
        await WaitUntilAsync(
            async () =>
            {
                var (_, _, reason) = await GetProbeAsync(api, "/readyz");
                return reason == "database_unreachable";
            },
            TimeSpan.FromSeconds(60),
            "database_unreachable should appear in /readyz before attempting agent registration");

        // Attempt registration with an active job while Postgres is down.
        // Use a nested scope so first is disposed before second is created — defensive against any
        // future FakeAgentClient change that might add to its static Connected map before InvokeAsync.
        {
            await using var first = new FakeAgentClient(agentId, "e2e");
            var ex = await Assert.ThrowsAsync<HubException>(
                () => first.ConnectWithActiveJobAsync(
                    api.ServerAddress,
                    E2EWebApplicationFactory.TestApiKey,
                    id.ToString(),
                    issue,
                    "repo-e2e"));
            ex.Message.Should().Contain("the work item store is unavailable");
        }

        // Restart Postgres and wait for recovery
        await Containers.StartPostgresAsync();
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should recover to 200 after Postgres is restarted");

        // Registration must succeed after recovery
        await using var second = new FakeAgentClient(agentId, "e2e");
        await second.ConnectWithActiveJobAsync(
            api.ServerAddress,
            E2EWebApplicationFactory.TestApiKey,
            id.ToString(),
            issue,
            "repo-e2e");

        // AgentOrphanRecoveryService restores the in-memory run when the agent re-registers
        var run = api.Services.GetRequiredService<IOrchestratorRunService>().GetRun(id.ToString());
        run.Should().NotBeNull();
        run!.IssueIdentifier.Value.Should().Be(issue);
    }

    [RequiresDockerFact]
    public async Task WhenPostgresIsDown_StatusPostShouldFailAndRunShouldCompleteAfterRecovery()
    {
        var issue = $"system-{Guid.NewGuid():N}";
        var agentId = $"system-agent-{Guid.NewGuid():N}";
        var api = await StartApiAsync();

        // Wait for /readyz to be ready
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should be ready before seeding a work item");

        // Seed a Running work item directly in the database
        var id = await SeedRunningWorkItemAsync(api, issue, agentId);

        // Master-key client — AuthorizeAgentForWorkItemAsync passes through callers without the
        // "agent" auth_kind claim (operator path), so the raw API key is sufficient here.
        using var client = new HttpClient
        {
            BaseAddress = new Uri(api.ServerAddress),
            Timeout = TimeSpan.FromSeconds(120)
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        // Use an anonymous type; the API's globally-registered JsonStringEnumConverter accepts
        // "Succeeded" as a string value for WorkItemStatus (no CodingAgent.Api using needed).
        var body = new { status = "Succeeded", agentId };

        // Stop Postgres and wait until the probe confirms the outage.
        // Without this guard, EF's retry loop (maxRetryCount: 3, maxRetryDelay: 5 s) may land a
        // successful connection through the still-draining container (Testcontainers' StopAsync
        // waits for the exited state, but the port can remain reachable briefly during SIGTERM
        // processing), causing the POST to return 200 and the IsSuccessStatusCode assertion to fail.
        await Containers.StopPostgresAsync();
        await WaitUntilAsync(
            async () =>
            {
                var (_, _, reason) = await GetProbeAsync(api, "/readyz");
                return reason == "database_unreachable";
            },
            TimeSpan.FromSeconds(60),
            "database_unreachable should appear in /readyz before posting status");

        // POST status while Postgres is down — the exact failure code is not specified
        // (EF retries can produce 500 or a timeout depending on timing)
        var response = await client.PostAsJsonAsync($"/api/work-items/{id}/status", body);
        response.IsSuccessStatusCode.Should().BeFalse();

        // Restart Postgres and wait for recovery
        await Containers.StartPostgresAsync();
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, "/readyz")).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60),
            "/readyz should recover to 200 after Postgres is restarted");

        // The same POST must now succeed
        var response2 = await client.PostAsJsonAsync($"/api/work-items/{id}/status", body);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the work item is Succeeded with CompletedAt set in the real database
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.SingleAsync(w => w.Id == id);
        item.Status.Should().Be(WorkItemStatus.Succeeded);
        item.CompletedAt.Should().NotBeNull();
    }
}
