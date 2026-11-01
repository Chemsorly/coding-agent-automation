using System.Net;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// Smoke tests that verify the Pipeline API starts correctly against real Postgres and Redis
/// containers, with all production hosted services active and no in-memory replacements.
///
/// <para>
/// These tests require Docker (via Testcontainers) and are isolated in the
/// <c>Category=System</c> trait so they do not run in the normal E2E shards (which filter on
/// <c>Category=E2E</c>) and do not run in CI's <c>build-and-test</c> job (which excludes both
/// E2E and Integration categories).
/// </para>
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class SystemSmokeTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    [RequiresDockerFact]
    public async Task WhenApiStartsOnRealPostgresAndRedis_ShouldBeReadyWithProductionServices()
    {
        // Act — start one API host against the real containers.
        // The base class disposes the host when the test ends.
        var api = await StartApiAsync();

        // Assert: liveness probe is immediately healthy
        var (healthzStatus, healthzField, _) = await GetProbeAsync(api, "/healthz");
        healthzStatus.Should().Be(HttpStatusCode.OK);
        healthzField.Should().Be("ok");

        // Assert: readiness probe eventually reaches "ready".
        // DatabaseReadinessMonitor (a real hosted service) marks the DB healthy asynchronously;
        // poll until it does or the 30-second timeout fires.
        await WaitUntilAsync(
            async () =>
            {
                var (status, field, _) = await GetProbeAsync(api, "/readyz");
                return status == HttpStatusCode.OK && field == "ready";
            },
            timeout: TimeSpan.FromSeconds(30),
            because: "/readyz should reach 200 ready once DatabaseReadinessMonitor marks the DB healthy");

        var (readyzStatus, readyzField, _) = await GetProbeAsync(api, "/readyz");
        readyzStatus.Should().Be(HttpStatusCode.OK);
        readyzField.Should().Be("ready");

        // Assert: Redis backplane is connected (AbortOnConnectFail=false means a wrong address
        // would let the host start but leave IsConnected=false — this assertion catches that).
        var mux = api.Services.GetRequiredService<IConnectionMultiplexer>();
        mux.IsConnected.Should().BeTrue("the Redis container is running and the connection string is valid");

        // Assert: DistributedAgentRegistryService is selected when Redis is present.
        var registry = api.Services.GetRequiredService<IAgentRegistryService>();
        registry.Should().BeOfType<DistributedAgentRegistryService>(
            "Redis is configured, so the distributed registry should be used");

        // Assert: DatabaseReadinessMonitor is present as a hosted service
        // (unlike ApiE2EWebApplicationFactory which removes all hosted services).
        var hostedServices = api.Services.GetServices<IHostedService>();
        hostedServices.Should().ContainSingle(
            s => s is DatabaseReadinessMonitor,
            "DatabaseReadinessMonitor must be running as a real hosted service in the System tier");

        // Assert: EF Core is using the real Postgres provider, all migrations are applied,
        // and no pending migrations remain.
        var dbFactory = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Database.IsNpgsql().Should().BeTrue("the System tier connects to a real Postgres container");

        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        appliedMigrations.Should().NotBeEmpty("at least the baseline migration must have been applied on startup");

        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();
        pendingMigrations.Should().BeEmpty("all migrations must be applied before the host is ready");
    }
}
