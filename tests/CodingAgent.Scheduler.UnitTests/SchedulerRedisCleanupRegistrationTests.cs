using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Verifies that <see cref="SchedulerServiceCollectionExtensions.AddSchedulerServices"/>
/// correctly registers <see cref="AgentRegistryCleanupService"/> and
/// <see cref="RunServiceCleanupService"/> under two scenarios:
///
/// <list type="bullet">
///   <item>
///     <b>No Redis</b> — The concrete singleton resolves to a no-op null object
///     (<see cref="NullAgentRegistryCleanupService"/> / <see cref="NullRunServiceCleanupService"/>),
///     so <c>GetRequiredService&lt;T&gt;()</c> is safe; the hosted-service entry forwards to the
///     same singleton.
///   </item>
///   <item>
///     <b>With Redis</b> — A registered <see cref="IRedisStore"/> causes the singleton factory to
///     return a real instance; the hosted-service factory returns the same singleton reference.
///   </item>
/// </list>
///
/// <para>
/// In <c>[Collection("Metrics")]</c> because <c>GetServices&lt;IHostedService&gt;()</c> resolves
/// all hosted services, including <c>WorkItemCountsService</c> which writes to process-global OTel
/// static callback fields. Running outside the collection would cause interference with
/// <see cref="SchedulerServicesRegistrationTests"/>.
/// </para>
/// </summary>
[Collection("Metrics")]
public sealed class SchedulerRedisCleanupRegistrationTests : IDisposable
{
    // ── Static callback field handles — reset in Dispose (mirrors SchedulerServicesRegistrationTests) ──

    private static readonly FieldInfo ActiveJobsCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_activeJobsCallback", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo TotalConnectionsCallbackField =
        typeof(PipelineTelemetry)
            .GetField("_totalConnectionsCallback", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo CredentialPoolAvailableCallbackField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailableCallback", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo CredentialPoolClaimedCallbackField =
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolClaimedCallback", BindingFlags.NonPublic | BindingFlags.Static)!;

    public void Dispose()
    {
        // Use .SetValue (not ?.) so that a renamed field causes a NullReferenceException here
        // rather than silently skipping the reset and leaking OTel callback state into other tests
        // in [Collection("Metrics")].
        ActiveJobsCallbackField.SetValue(null, null);
        TotalConnectionsCallbackField.SetValue(null, null);
        CredentialPoolAvailableCallbackField.SetValue(null, null);
        CredentialPoolClaimedCallbackField.SetValue(null, null);
    }

    // ── Provider factories ──────────────────────────────────────────────

    private static IConfiguration BuildNoRedisConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable the dispatch loop — consistent with every other test provider in this assembly.
                ["Scheduler:Dispatch:Enabled"] = "false",
                // No Redis connection string → IRedisStore not registered by AddSchedulerServices.
            })
            .Build();

    private static IConfiguration BuildWithFakeRedisConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Dispatch:Enabled"] = "false",
                // No Redis connection string — IRedisStore is registered manually before AddSchedulerServices
                // so the connection-string guard inside AddSchedulerServices skips its own registration.
            })
            .Build();

    private static ServiceProvider BuildNoRedisProvider()
    {
        var config = BuildNoRedisConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSchedulerServices("http://localhost:5000", "test-key", config);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildWithFakeRedisProvider()
    {
        var config = BuildWithFakeRedisConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        // Register a mock IRedisStore *before* AddSchedulerServices so that the connection-string
        // guard inside AddSchedulerServices sees no connection string and skips its own registration,
        // leaving this mock as the sole IRedisStore in the container.
        services.AddSingleton<IRedisStore>(new Mock<IRedisStore>().Object);
        services.AddSchedulerServices("http://localhost:5000", "test-key", config);
        return services.BuildServiceProvider();
    }

    // ── AgentRegistryCleanupService — no Redis ────────────────────────────

    [Fact]
    public async Task AgentRegistryCleanupService_WithoutRedis_GetRequiredServiceReturnsNonNullInstance()
    {
        // Arrange
        await using var provider = BuildNoRedisProvider();

        // Act
        var act = () => provider.GetRequiredService<AgentRegistryCleanupService>();

        // Assert — GetRequiredService must be safe in every configuration.
        act.Should().NotThrow("GetRequiredService<T>() must be safe when IRedisStore is not registered")
            .Which.Should().BeOfType<NullAgentRegistryCleanupService>(
                "the no-op null object stands in for the real service when IRedisStore is not registered");
    }

    [Fact]
    public async Task AgentRegistryCleanupService_WithoutRedis_HostedServiceIsSameNullObjectInstance()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();
        var singleton = provider.GetRequiredService<AgentRegistryCleanupService>();

        // Assert — the hosted-service entry forwards to the singleton, so the host starts the
        // no-op null object and never a second instance.
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => ReferenceEquals(s, singleton),
            "IHostedService must resolve to the same null-object singleton when IRedisStore is absent");
        singleton.Should().BeOfType<NullAgentRegistryCleanupService>();
    }

    // ── AgentRegistryCleanupService — with real store ─────────────────────

    [Fact]
    public async Task AgentRegistryCleanupService_WithRedis_ConcreteSingletonIsNotNull()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();

        // Assert — a registered IRedisStore causes the singleton factory to return a real instance.
        provider.GetService<AgentRegistryCleanupService>().Should().BeOfType<AgentRegistryCleanupService>(
            "the singleton factory must return the real instance, not the null object, when IRedisStore is registered");
    }

    [Fact]
    public async Task AgentRegistryCleanupService_WithRedis_HostedServiceIsSameSingletonInstance()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();
        var singleton = provider.GetRequiredService<AgentRegistryCleanupService>();

        // Assert — the hosted-service factory resolves the same singleton (not a second instance).
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => ReferenceEquals(s, singleton),
            "IHostedService must resolve to the same singleton instance when IRedisStore is registered");
    }

    // ── RunServiceCleanupService — no Redis ───────────────────────────────

    [Fact]
    public async Task RunServiceCleanupService_WithoutRedis_GetRequiredServiceReturnsNonNullInstance()
    {
        // Arrange
        await using var provider = BuildNoRedisProvider();

        // Act
        var act = () => provider.GetRequiredService<RunServiceCleanupService>();

        // Assert — GetRequiredService must be safe in every configuration.
        act.Should().NotThrow("GetRequiredService<T>() must be safe when IRedisStore is not registered")
            .Which.Should().BeOfType<NullRunServiceCleanupService>(
                "the no-op null object stands in for the real service when IRedisStore is not registered");
    }

    [Fact]
    public async Task RunServiceCleanupService_WithoutRedis_HostedServiceIsSameNullObjectInstance()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();
        var singleton = provider.GetRequiredService<RunServiceCleanupService>();

        // Assert — the hosted-service entry forwards to the singleton, so the host starts the
        // no-op null object and never a second instance.
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => ReferenceEquals(s, singleton),
            "IHostedService must resolve to the same null-object singleton when IRedisStore is absent");
        singleton.Should().BeOfType<NullRunServiceCleanupService>();
    }

    // ── RunServiceCleanupService — with real store ────────────────────────

    [Fact]
    public async Task RunServiceCleanupService_WithRedis_ConcreteSingletonIsNotNull()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();

        // Assert — a registered IRedisStore causes the singleton factory to return a real instance.
        provider.GetService<RunServiceCleanupService>().Should().BeOfType<RunServiceCleanupService>(
            "the singleton factory must return the real instance, not the null object, when IRedisStore is registered");
    }

    [Fact]
    public async Task RunServiceCleanupService_WithRedis_HostedServiceIsSameSingletonInstance()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();
        var singleton = provider.GetRequiredService<RunServiceCleanupService>();

        // Assert — the hosted-service factory resolves the same singleton (not a second instance).
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => ReferenceEquals(s, singleton),
            "IHostedService must resolve to the same singleton instance when IRedisStore is registered");
    }
}
