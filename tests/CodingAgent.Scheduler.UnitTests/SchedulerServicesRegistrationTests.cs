using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Characterization tests that pin the six background services registered by
/// <see cref="SchedulerServiceCollectionExtensions.AddSchedulerServices"/> as
/// <see cref="IHostedService"/> singletons.
///
/// Added as a prerequisite before the IServiceScopeFactory refactor (issue #3992).
/// Each test asserts both concrete-type resolution and hosted-service registration so
/// that any future change that accidentally drops a service from DI is caught.
///
/// Placed in [Collection("Metrics")] because constructing WorkItemCountsService (which
/// happens when building the provider) registers OTel gauge callbacks on static fields.
/// The Dispose method resets those fields after each test.
/// </summary>
[Collection("Metrics")]
public sealed class SchedulerServicesRegistrationTests : IDisposable
{
    // ── Static callback field handles — reset in Dispose ──────────────────

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
        ActiveJobsCallbackField?.SetValue(null, null);
        TotalConnectionsCallbackField?.SetValue(null, null);
        CredentialPoolAvailableCallbackField?.SetValue(null, null);
        CredentialPoolClaimedCallbackField?.SetValue(null, null);
    }

    // ── Provider factory ──────────────────────────────────────────────────

    private static IConfiguration BuildMinimalConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable the dispatch loop — WorkItemDispatchLoop is not in scope for issue #3992.
                ["Scheduler:Dispatch:Enabled"] = "false",
                // No Redis — avoids a real network connection.
            })
            .Build();

    private static ServiceProvider BuildProvider()
    {
        var config = BuildMinimalConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        // IConfiguration must be registered so that LeaderElectionOptions.Configure<IConfiguration>
        // can bind from config when GetServices<IHostedService>() triggers LeaderElectionService resolution.
        services.AddSingleton<IConfiguration>(config);
        services.AddSchedulerServices("http://localhost:5000", "test-key", config);
        return services.BuildServiceProvider();
    }

    // ── LoopWatchdogService ────────────────────────────────────────────────

    [Fact]
    public async Task LoopWatchdogService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<LoopWatchdogService>();
        var second = provider.GetRequiredService<LoopWatchdogService>();

        first.Should().BeOfType<LoopWatchdogService>();
        first.Should().BeSameAs(second, "LoopWatchdogService must be registered as a singleton");
    }

    [Fact]
    public async Task LoopWatchdogService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is LoopWatchdogService,
            "LoopWatchdogService must be registered as IHostedService");
    }

    // ── OrphanedLabelRecoveryService ──────────────────────────────────────

    [Fact]
    public async Task OrphanedLabelRecoveryService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<OrphanedLabelRecoveryService>();
        var second = provider.GetRequiredService<OrphanedLabelRecoveryService>();

        first.Should().BeOfType<OrphanedLabelRecoveryService>();
        first.Should().BeSameAs(second, "OrphanedLabelRecoveryService must be registered as a singleton");
    }

    [Fact]
    public async Task OrphanedLabelRecoveryService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is OrphanedLabelRecoveryService,
            "OrphanedLabelRecoveryService must be registered as IHostedService");
    }

    // ── FeedbackCommentRelayService ────────────────────────────────────────

    [Fact]
    public async Task FeedbackCommentRelayService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<FeedbackCommentRelayService>();
        var second = provider.GetRequiredService<FeedbackCommentRelayService>();

        first.Should().BeOfType<FeedbackCommentRelayService>();
        first.Should().BeSameAs(second, "FeedbackCommentRelayService must be registered as a singleton");
    }

    [Fact]
    public async Task FeedbackCommentRelayService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is FeedbackCommentRelayService,
            "FeedbackCommentRelayService must be registered as IHostedService");
    }

    // ── AutoConsolidationService ───────────────────────────────────────────

    [Fact]
    public async Task AutoConsolidationService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<AutoConsolidationService>();
        var second = provider.GetRequiredService<AutoConsolidationService>();

        first.Should().BeOfType<AutoConsolidationService>();
        first.Should().BeSameAs(second, "AutoConsolidationService must be registered as a singleton");
    }

    [Fact]
    public async Task AutoConsolidationService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is AutoConsolidationService,
            "AutoConsolidationService must be registered as IHostedService");
    }

    // ── WorkItemCountsService ──────────────────────────────────────────────

    [Fact]
    public async Task WorkItemCountsService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<WorkItemCountsService>();
        var second = provider.GetRequiredService<WorkItemCountsService>();

        first.Should().BeOfType<WorkItemCountsService>();
        first.Should().BeSameAs(second, "WorkItemCountsService must be registered as a singleton");
    }

    [Fact]
    public async Task WorkItemCountsService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is WorkItemCountsService,
            "WorkItemCountsService must be registered as IHostedService");
    }

    // ── RetentionSweepSchedulerService ────────────────────────────────────

    [Fact]
    public async Task RetentionSweepSchedulerService_RegisteredAsConcreteSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<RetentionSweepSchedulerService>();
        var second = provider.GetRequiredService<RetentionSweepSchedulerService>();

        first.Should().BeOfType<RetentionSweepSchedulerService>();
        first.Should().BeSameAs(second, "RetentionSweepSchedulerService must be registered as a singleton");
    }

    [Fact]
    public async Task RetentionSweepSchedulerService_RegisteredAsIHostedService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IHostedService>().Should().ContainSingle(
            s => s is RetentionSweepSchedulerService,
            "RetentionSweepSchedulerService must be registered as IHostedService");
    }
}
