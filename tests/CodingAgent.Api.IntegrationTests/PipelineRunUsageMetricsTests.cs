using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Tests for the new pipeline.run.tokens, pipeline.run.cost_usd, pipeline.run.agent_sessions,
/// and pipeline.run.agent_time metric emission in
/// <see cref="WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync"/>.
/// AC3: The new counters are exported by the API (unit tests for phase normalization).
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class PipelineRunUsageMetricsTests
{
    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"UsageMetrics-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedRunningItemAsync(DbContextOptions<PipelineDbContext> opts)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Running,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await using var ctx = new UsageTestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemTransitionService CreateTransitionService(DbContextOptions<PipelineDbContext> opts)
        => new(new UsageTestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance);

    private static WorkItemStatusTransitionService CreateService(DbContextOptions<PipelineDbContext> opts)
    {
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager.Setup(m => m.FailRunAsync(
                It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);
        lifecycleManager.Setup(m => m.CancelRunAsync(
                It.IsAny<RunId>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);
        return new WorkItemStatusTransitionService(
            CreateTransitionService(opts), lifecycleManager.Object,
            new UsageTestDbContextFactory(opts));
    }

    private static string SerializePayload(JobCompletionPayload payload)
        => JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);

    // ── pipeline.run.tokens ────────────────────────────────────────────────────

    [Fact]
    public async Task RunTokens_EmittedForEachPhase_WithNormalizedPhaseTags()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var observed = new ConcurrentBag<(string Phase, string Provider, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.tokens") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string phase = "", provider = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                else if (tag.Key == "provider") provider = tag.Value?.ToString() ?? "";
            }
            observed.Add((phase, provider, measurement));
        });
        listener.Start();

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderType = AgentProviderType.KiroCli,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = new PhaseUsage(1000, null, Sessions: 1, AgentSeconds: 60.0),
                ["codegen"] = new PhaseUsage(5000, 0.10m, Sessions: 2, AgentSeconds: 300.0),
                ["review_Correctness"] = new PhaseUsage(2000, null, Sessions: 1, AgentSeconds: 90.0)
            }
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        // analysis → "analysis", codegen → "codegen", review_Correctness → "review"
        // TODO: [WARNING] This test does not assert that no extra phases are emitted. If
        // RecordPhaseUsageMetrics emitted an unexpected extra entry (e.g. a normalization bug
        // producing an unknown key), nothing would catch it. Add observed.Should().HaveCount(3)
        // to enforce that exactly the three expected phases are emitted.
        observed.Should().Contain(o => o.Phase == "analysis" && o.Value == 1000 && o.Provider == "kiro");
        observed.Should().Contain(o => o.Phase == "codegen" && o.Value == 5000 && o.Provider == "kiro");
        observed.Should().Contain(o => o.Phase == "review" && o.Value == 2000 && o.Provider == "kiro");
    }

    [Fact]
    public async Task RunCostUsd_EmittedForPhasesWithCost()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var observed = new ConcurrentBag<(string Phase, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.cost_usd") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string phase = "";
            foreach (var tag in tags)
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
            observed.Add((phase, measurement));
        });
        listener.Start();

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderType = AgentProviderType.OpenCode,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["codegen"] = new PhaseUsage(5000, 0.10m, Sessions: 1),
                ["analysis"] = new PhaseUsage(1000, null) // no cost — should not emit
            }
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        // TODO: [WARNING] Assertion only checks o.Value > 0, not the precise value. If a
        // unit-conversion bug caused 0.10m (USD) to be emitted as 0.001, this test would still
        // pass. Use a precise assertion: o => o.Phase == "codegen" && Math.Abs(o.Value - 0.10) < 0.0001
        observed.Should().Contain(o => o.Phase == "codegen" && o.Value > 0);
        // analysis has null cost — no cost_usd entry expected for it
        observed.Should().NotContain(o => o.Phase == "analysis");
    }

    [Fact]
    public async Task RunAgentSessions_EmittedForPhasesWithPositiveSessions()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var observed = new ConcurrentBag<(string Phase, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.agent_sessions") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string phase = "";
            foreach (var tag in tags)
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
            observed.Add((phase, measurement));
        });
        listener.Start();

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderType = AgentProviderType.KiroCli,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["codegen"] = new PhaseUsage(5000, null, Sessions: 3),
                ["analysis_review"] = new PhaseUsage(500, null, Sessions: 0) // no sessions → not emitted
            }
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        observed.Should().Contain(o => o.Phase == "codegen" && o.Value == 3);
        observed.Should().NotContain(o => o.Phase == "analysis_review");
    }

    [Fact]
    public async Task RunAgentTime_EmittedForPhasesWithPositiveSeconds()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var observed = new ConcurrentBag<(string Phase, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.agent_time") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string phase = "";
            foreach (var tag in tags)
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
            observed.Add((phase, measurement));
        });
        listener.Start();

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderType = AgentProviderType.KiroCli,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = new PhaseUsage(1000, null, Sessions: 1, AgentSeconds: 120.5),
                ["analysis_review"] = new PhaseUsage(500, null, Sessions: 0, AgentSeconds: 0.0) // no time → not emitted
            }
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        // TODO: [WARNING] Assertion only checks o.Value > 0, not the exact value (120.5 seconds).
        // A factor-of-1000 conversion bug (seconds → milliseconds) would not be caught. Use a
        // precise assertion: o => o.Phase == "analysis" && Math.Abs(o.Value - 120.5) < 0.001
        observed.Should().Contain(o => o.Phase == "analysis" && o.Value > 0);
        observed.Should().NotContain(o => o.Phase == "analysis_review");
    }

    [Fact]
    public async Task AllCounters_UseUnknownProvider_WhenProviderTypeIsNull()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var providers = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.tokens") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "provider") providers.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        // Old agent pod — ProviderType is null
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ProviderType = null,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["codegen"] = new PhaseUsage(1000, null, Sessions: 1)
            }
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        providers.Should().Contain("unknown");
        // TODO: [WARNING] This test only subscribes to pipeline.run.tokens and verifies the
        // provider tag for that counter. The payload also has Sessions: 1, which would trigger
        // pipeline.run.agent_sessions emission. If the provider-tag mapping in
        // RecordPhaseUsageMetrics used a different default for RunAgentSessions.Add, this test
        // would not catch it. Consider also asserting "unknown" on the agent_sessions counter.
    }

    [Fact]
    public async Task NoCounters_WhenPhaseBreakdownIsNull()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);

        var emitted = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name is "pipeline.run.tokens" or "pipeline.run.cost_usd"
                or "pipeline.run.agent_sessions" or "pipeline.run.agent_time")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => emitted.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => emitted.Add(instrument.Name));
        listener.Start();

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = null // no breakdown
        };

        var service = CreateService(opts);
        await service.TransitionAsync(
            item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                AgentId = "agent-1",
                Result = SerializePayload(payload)
            },
            CancellationToken.None,
            awaitTelemetry: true);

        emitted.Should().BeEmpty();
    }

    // ── Test infrastructure ────────────────────────────────────────────────────

    private sealed class UsageTestPipelineDbContext : PipelineDbContext
    {
        public UsageTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexes = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var idx in indexes)
                    entityType.RemoveIndex(idx);
            }
        }
    }

    private sealed class UsageTestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public UsageTestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new UsageTestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
