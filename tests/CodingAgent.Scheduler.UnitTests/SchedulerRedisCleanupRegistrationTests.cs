using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Telemetry;
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
///     <b>No Redis</b> — The concrete singleton resolves to <see langword="null"/> (the factory
///     returns <c>null!</c>) and the hosted-service fallback (<c>??</c> branch) fires, constructing
///     the service with a <c>NullRedisStore</c>. No <see cref="NullReferenceException"/> is thrown.
///     (<c>NullRedisStore</c> is <c>internal sealed</c> in <c>CodingAgent.Orchestration</c> so the
///     assertion is indirect: null singleton + service present in <see cref="IHostedService"/> list
///     is the only code path that could have produced the instance via the fallback branch.)
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
    public async Task AgentRegistryCleanupService_WithoutRedis_ConcreteSingletonIsNull()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();

        // Assert — the singleton factory returns null! when IRedisStore is absent.
        provider.GetService<AgentRegistryCleanupService>().Should().BeNull(
            "the singleton factory returns null! when IRedisStore is not registered");
    }

    [Fact]
    public async Task AgentRegistryCleanupService_WithoutRedis_RegisteredAsIHostedServiceViaFallback()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();

        // Assert — the hosted-service fallback (the ?? branch) fired and constructed the service
        // with a NullRedisStore. This is the only code path that can produce an
        // AgentRegistryCleanupService instance in IHostedService when the singleton is null,
        // confirming the NullRedisStore fallback was used.
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is AgentRegistryCleanupService,
            "the ?? fallback branch must register an AgentRegistryCleanupService even without Redis");
    }

    // ── AgentRegistryCleanupService — with real store ─────────────────────

    [Fact]
    public async Task AgentRegistryCleanupService_WithRedis_ConcreteSingletonIsNotNull()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();

        // Assert — a registered IRedisStore causes the singleton factory to return a real instance.
        provider.GetService<AgentRegistryCleanupService>().Should().NotBeNull(
            "the singleton factory must return a real instance when IRedisStore is registered");
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
    public async Task RunServiceCleanupService_WithoutRedis_ConcreteSingletonIsNull()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();

        // Assert — the singleton factory returns null! when IRedisStore is absent.
        provider.GetService<RunServiceCleanupService>().Should().BeNull(
            "the singleton factory returns null! when IRedisStore is not registered");
    }

    [Fact]
    public async Task RunServiceCleanupService_WithoutRedis_RegisteredAsIHostedServiceViaFallback()
    {
        // Arrange / Act
        await using var provider = BuildNoRedisProvider();

        // Assert — the hosted-service fallback (the ?? branch) fired and constructed the service
        // with a NullRedisStore. This is the only code path that can produce a
        // RunServiceCleanupService instance in IHostedService when the singleton is null,
        // confirming the NullRedisStore fallback was used.
        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is RunServiceCleanupService,
            "the ?? fallback branch must register a RunServiceCleanupService even without Redis");
    }

    // ── RunServiceCleanupService — with real store ────────────────────────

    [Fact]
    public async Task RunServiceCleanupService_WithRedis_ConcreteSingletonIsNotNull()
    {
        // Arrange / Act
        await using var provider = BuildWithFakeRedisProvider();

        // Assert — a registered IRedisStore causes the singleton factory to return a real instance.
        provider.GetService<RunServiceCleanupService>().Should().NotBeNull(
            "the singleton factory must return a real instance when IRedisStore is registered");
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
