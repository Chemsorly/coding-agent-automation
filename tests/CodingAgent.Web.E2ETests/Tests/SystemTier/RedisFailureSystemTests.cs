using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// System-tier tests that verify the health probes and <c>/api/ui/health</c> against a real Redis that
/// stops and comes back while the API host keeps running. The probes decide whether Kubernetes restarts
/// a pod (<c>/healthz</c>) or takes it out of rotation (<c>/readyz</c>): a Redis outage must do the
/// second without the first, and a drain must be reported before any dependency problem.
/// <c>HealthProbeRedisTests</c> in the API integration tests covers the probes against a Redis that was
/// never reachable; these tests cover the stop and the return.
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class RedisFailureSystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    private const string ReadyzPath = "/readyz";
    private const string HealthzPath = "/healthz";
    private const string BackplaneDisconnectedReason = "redis_backplane_disconnected";

    private static readonly JsonSerializerOptions UiJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task WaitForReadyAsync(SystemApiFactory api) =>
        await WaitUntilAsync(async () => (await GetProbeAsync(api, ReadyzPath)).Status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(60), "/readyz did not become ready");

    /// <summary>
    /// Reads <c>GET /api/ui/health</c>. Returns <c>null</c> for a non-success response so that polling
    /// conditions built on it return <c>false</c> instead of throwing.
    /// </summary>
    private static async Task<UiHealthResponse?> GetUiHealthAsync(SystemApiFactory api)
    {
        using var client = CreateUiClient(api);
        using var response = await client.GetAsync("/api/ui/health");
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<UiHealthResponse>(UiJsonOptions);
    }

    /// <summary>
    /// Stops the Redis container and waits until <c>/readyz</c> reports the disconnected backplane.
    /// <c>IsConnected</c> flips after the socket closes, not when the stop returns, so the wait is a poll.
    /// </summary>
    private async Task StopRedisAndWaitForOutageAsync(SystemApiFactory api)
    {
        await Containers.StopRedisAsync();
        await WaitUntilAsync(
            async () => (await GetProbeAsync(api, ReadyzPath)).Reason == BackplaneDisconnectedReason,
            TimeSpan.FromSeconds(60),
            "/readyz should report redis_backplane_disconnected after Redis is stopped");
    }

    [RequiresDockerFact]
    public async Task WhenRedisStops_ShouldTakePodOutOfRotationButKeepItAlive()
    {
        // Arrange
        var api = await StartApiAsync();
        await WaitForReadyAsync(api);

        var before = await GetUiHealthAsync(api);
        before.Should().NotBeNull();
        before!.RedisConnected.Should().BeTrue("the Redis container is running and /readyz is ready");

        // Act
        await StopRedisAndWaitForOutageAsync(api);

        // Assert: readiness fails with the backplane reason, so the pod leaves the Service.
        var (readyzStatus, readyzField, readyzReason) = await GetProbeAsync(api, ReadyzPath);
        readyzStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
        readyzField.Should().Be("unhealthy");
        readyzReason.Should().Be(BackplaneDisconnectedReason);

        // Assert: the UI sidebar sees Redis down and the database up.
        await WaitUntilAsync(
            async () => await GetUiHealthAsync(api) is { RedisConnected: false, DatabaseConnected: true },
            TimeSpan.FromSeconds(60),
            "/api/ui/health should report RedisConnected=false and DatabaseConnected=true");

        // Assert: liveness stays 200, so Kubernetes does not restart the pod. /healthz is 200 before the
        // outage too, so it is read once, only now that the outage is visible on both other surfaces.
        var (healthzStatus, healthzField, healthzReason) = await GetProbeAsync(api, HealthzPath);
        healthzStatus.Should().Be(HttpStatusCode.OK);
        healthzField.Should().Be("ok");
        healthzReason.Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task WhenRedisReturns_ShouldRecoverWithoutHostRestart()
    {
        // Arrange
        var api = await StartApiAsync();
        await WaitForReadyAsync(api);
        await StopRedisAndWaitForOutageAsync(api);

        // Act
        await Containers.StartRedisAsync();

        // Assert: the same host reconnects. Reconnect back-off can take up to about a minute.
        await WaitUntilAsync(
            async () =>
            {
                var (status, statusField, _) = await GetProbeAsync(api, ReadyzPath);
                return status == HttpStatusCode.OK && statusField == "ready";
            },
            TimeSpan.FromSeconds(90),
            "/readyz should return to 200 ready after Redis is started again");

        await WaitUntilAsync(
            async () => await GetUiHealthAsync(api) is { RedisConnected: true },
            TimeSpan.FromSeconds(90),
            "/api/ui/health should report RedisConnected=true after Redis is started again");

        var (healthzStatus, healthzField, _) = await GetProbeAsync(api, HealthzPath);
        healthzStatus.Should().Be(HttpStatusCode.OK);
        healthzField.Should().Be("ok");
    }

    [RequiresDockerFact]
    public async Task WhenDrainStartsWhileRedisIsDown_ShouldReportDrainingFirst()
    {
        // Arrange
        var api = await StartApiAsync();
        await WaitForReadyAsync(api);
        await StopRedisAndWaitForOutageAsync(api);

        // Act
        await api.Services.GetServices<IHostedService>().OfType<ReadinessDrainService>().Single()
            .StoppingAsync(CancellationToken.None);

        // Assert: draining is a pre-shutdown signal and is reported before any dependency problem.
        var (status, statusField, reason) = await GetProbeAsync(api, ReadyzPath);
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        statusField.Should().Be("draining");
        reason.Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task WhenDrainStartsOnHealthyHost_ShouldNotFailLiveness()
    {
        // Arrange
        var api = await StartApiAsync();
        await WaitForReadyAsync(api);

        // Act
        await api.Services.GetServices<IHostedService>().OfType<ReadinessDrainService>().Single()
            .StoppingAsync(CancellationToken.None);

        // Assert: draining takes the pod out of rotation and must not get it killed.
        var (readyzStatus, readyzField, _) = await GetProbeAsync(api, ReadyzPath);
        readyzStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
        readyzField.Should().Be("draining");

        var (healthzStatus, healthzField, _) = await GetProbeAsync(api, HealthzPath);
        healthzStatus.Should().Be(HttpStatusCode.OK);
        healthzField.Should().Be("ok");
    }
}
