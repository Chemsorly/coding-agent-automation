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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Tests for the per-phase LLM usage counter emission in
/// <see cref="WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync"/>
/// when <see cref="JobCompletionPayload.PhaseBreakdown"/> is populated.
///
/// Verifies AC: "The new counters are exported by the API (unit tests for phase normalization)."
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class RunUsageMetricsTests
{
    // ── Infrastructure ────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"RunUsage-{Guid.NewGuid():N}")
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
        await using var ctx = new LocalTestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemStatusTransitionService CreateService(DbContextOptions<PipelineDbContext> opts)
    {
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);
        var dbFactory = new LocalTestDbContextFactory(opts);
        return new WorkItemStatusTransitionService(
            new WorkItemTransitionService(dbFactory, NullLogger<WorkItemTransitionService>.Instance),
            lifecycleManager.Object, dbFactory);
    }

    private static string SerializePayload(JobCompletionPayload payload)
        => JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);

    /// <summary>
    /// Captures long counter measurements from the given instrument name, skipping Add(0) pre-init.
    /// </summary>
    private static (MeterListener Listener,
        ConcurrentBag<(long Value, Dictionary<string, object?> Tags)> Bag)
        SetupLongListener(string instrumentName)
    {
        var bag = new ConcurrentBag<(long, Dictionary<string, object?>)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (value == 0) return;
            bag.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();
        return (listener, bag);
    }

    /// <summary>
    /// Captures double counter measurements from the given instrument name, skipping Add(0) pre-init.
    /// </summary>
    private static (MeterListener Listener,
        ConcurrentBag<(double Value, Dictionary<string, object?> Tags)> Bag)
        SetupDoubleListener(string instrumentName)
    {
        var bag = new ConcurrentBag<(double, Dictionary<string, object?>)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            if (value == 0) return;
            bag.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.Start();
        return (listener, bag);
    }

    // ── Counter emission tests ─────────────────────────────────────────────────
    // TODO: None of the counter emission tests assert on the run_type tag. The run_type tag is a
    // required dimension of all four new counters and is derived from WorkItemEntity.TaskType.
    // A bug that emits the wrong run_type or omits it would not be caught. Add assertions like
    // measurement.Tags["run_type"].Should().Be("implementation") to each emission test, or add a
    // dedicated test that seeds an item with a specific TaskType and verifies the emitted run_type tag.

    [Fact]
    public async Task PhaseBreakdown_Tokens_EmitsRunTokensCounter()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (tokensListener, tokensBag) = SetupLongListener("pipeline.run.tokens");
        using var _ = tokensListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["analysis"] = new() { Tokens = 500, Provider = "kiro" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var measurement = tokensBag.Should().ContainSingle(m => m.Value == 500).Which;
        measurement.Tags["phase"].Should().Be("analysis");
        measurement.Tags["provider"].Should().Be("kiro");
    }

    [Fact]
    public async Task PhaseBreakdown_Cost_EmitsRunCostUsdCounter()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (costListener, costBag) = SetupDoubleListener("pipeline.run.cost_usd");
        using var _ = costListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["codegen"] = new() { Tokens = 200, Cost = 0.05m, Provider = "opencode" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var measurement = costBag.Should().ContainSingle(m => Math.Abs(m.Value - 0.05) < 0.001).Which;
        measurement.Tags["phase"].Should().Be("codegen");
        measurement.Tags["provider"].Should().Be("opencode");
    }

    [Fact]
    public async Task PhaseBreakdown_Sessions_EmitsRunAgentSessionsCounter()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (sessionsListener, sessionsBag) = SetupLongListener("pipeline.run.agent_sessions");
        using var _ = sessionsListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["analysis"] = new() { SessionCount = 3, Provider = "kiro", Model = "claude-sonnet-4-5" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var measurement = sessionsBag.Should().ContainSingle(m => m.Value == 3).Which;
        measurement.Tags["phase"].Should().Be("analysis");
        measurement.Tags["provider"].Should().Be("kiro");
        measurement.Tags["model"].Should().Be("claude-sonnet-4-5");
    }

    [Fact]
    public async Task PhaseBreakdown_AgentTime_EmitsRunAgentTimeCounter()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (timeListener, timeBag) = SetupDoubleListener("pipeline.run.agent_time");
        using var _ = timeListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["codegen"] = new() { AgentTimeSeconds = 120.5, Provider = "kiro", SessionCount = 1 }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var measurement = timeBag.Should().ContainSingle(m => Math.Abs(m.Value - 120.5) < 0.01).Which;
        measurement.Tags["phase"].Should().Be("codegen");
        measurement.Tags["provider"].Should().Be("kiro");
    }

    [Fact]
    public async Task PhaseBreakdown_PerReviewerPhase_NormalizesToReview()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (tokensListener, tokensBag) = SetupLongListener("pipeline.run.tokens");
        using var _ = tokensListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["review_correctness"] = new() { Tokens = 100, Provider = "kiro" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var measurement = tokensBag.Should().ContainSingle(m => m.Value == 100).Which;
        measurement.Tags["phase"].Should().Be("review",
            "review_correctness must normalize to 'review'");
    }

    [Fact]
    public async Task PhaseBreakdown_Null_DoesNotEmitUsageCounters()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (tokensListener, tokensBag) = SetupLongListener("pipeline.run.tokens");
        using var _ = tokensListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = null
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        tokensBag.Should().BeEmpty("null phase breakdown must not emit any counter increments");
    }

    [Fact]
    public async Task PhaseBreakdown_ZeroTokens_DoesNotEmitTokensCounter()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (tokensListener, tokensBag) = SetupLongListener("pipeline.run.tokens");
        using var _ = tokensListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["analysis"] = new() { Tokens = 0, Provider = "kiro" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        tokensBag.Should().BeEmpty("zero tokens must not emit a counter increment");
    }

    [Fact]
    public void PreInitCounters_IncludeAllRunUsageCounters()
    {
        var tokenSeries = new ConcurrentBag<(long Value, Dictionary<string, object?> Tags)>();
        var sessionSeries = new ConcurrentBag<(long Value, Dictionary<string, object?> Tags)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && (instrument.Name == "pipeline.run.tokens" || instrument.Name == "pipeline.run.agent_sessions"))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagDict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (instrument.Name == "pipeline.run.tokens")
                tokenSeries.Add((value, tagDict));
            else if (instrument.Name == "pipeline.run.agent_sessions")
                sessionSeries.Add((value, tagDict));
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // 5 run_types × 9 phases × 3 providers = 135 series
        tokenSeries.Should().HaveCount(135,
            "5 run_types × 9 phases × 3 providers = 135 pre-init series for pipeline.run.tokens");

        sessionSeries.Should().HaveCount(135,
            "pipeline.run.agent_sessions: model='unknown' is fixed in pre-init, same 135 series");

        // Verify all 9 phases are present
        var phases = tokenSeries.Select(m => m.Tags.GetValueOrDefault("phase")?.ToString())
            .Where(p => p is not null).Distinct().Order().ToList();
        phases.Should().BeEquivalentTo(PipelineTelemetry.RunPhases.All);

        // Verify all 3 providers are present
        var providers = tokenSeries.Select(m => m.Tags.GetValueOrDefault("provider")?.ToString())
            .Where(p => p is not null).Distinct().Order().ToList();
        providers.Should().BeEquivalentTo(PipelineTelemetry.RunProviders.All);
    }

    // ── Test infrastructure ───────────────────────────────────────────────────

    private sealed class LocalTestPipelineDbContext : PipelineDbContext
    {
        public LocalTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = ValueGenerated.Never;
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

    private sealed class LocalTestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public LocalTestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new LocalTestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
