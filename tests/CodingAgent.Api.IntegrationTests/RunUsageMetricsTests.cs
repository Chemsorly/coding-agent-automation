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
            lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
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

        // 6 run_types × 10 phases × 4 providers = 240 series
        tokenSeries.Should().HaveCount(240,
            "6 run_types × 10 phases × 4 providers = 240 pre-init series for pipeline.run.tokens");

        sessionSeries.Should().HaveCount(240,
            "pipeline.run.agent_sessions: model='unknown' is fixed in pre-init, same 240 series");

        // Verify all 10 phases are present
        var phases = tokenSeries.Select(m => m.Tags.GetValueOrDefault("phase")?.ToString())
            .Where(p => p is not null).Distinct().Order().ToList();
        phases.Should().BeEquivalentTo(PipelineTelemetry.RunPhases.All);

        // Verify all 4 providers are present
        var providers = tokenSeries.Select(m => m.Tags.GetValueOrDefault("provider")?.ToString())
            .Where(p => p is not null).Distinct().Order().ToList();
        providers.Should().BeEquivalentTo(PipelineTelemetry.RunProviders.All);
    }

    [Fact]
    public void PreInitCounters_IncludeUsageDetailCounters()
    {
        var counts = new ConcurrentDictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name is "pipeline.run.token_usage" or "pipeline.run.agent_turns"
                    or "pipeline.run.web_search_requests" or "pipeline.run.billing_cost_usd"
                    or "pipeline.run.rate_limit_events")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => counts.AddOrUpdate(instrument.Name, 1, (_, c) => c + 1));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => counts.AddOrUpdate(instrument.Name, 1, (_, c) => c + 1));
        listener.Start();

        Program.EmitPreInitCounters();

        counts["pipeline.run.token_usage"].Should().Be(120, "6 run_types × 4 providers × 5 token types");
        counts["pipeline.run.billing_cost_usd"].Should().Be(72, "6 run_types × 4 providers × 3 billing modes");
        counts["pipeline.run.agent_turns"].Should().Be(24, "6 run_types × 4 providers");
        counts["pipeline.run.web_search_requests"].Should().Be(24, "6 run_types × 4 providers");
        counts["pipeline.run.rate_limit_events"].Should().Be(15, "claude × 5 windows × 3 statuses");
    }

    [Fact]
    public async Task PhaseBreakdown_UsageDetails_EmitsDetailCounters()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (tokenListener, tokenBag) = SetupLongListener("pipeline.run.token_usage");
        var (turnsListener, turnsBag) = SetupLongListener("pipeline.run.agent_turns");
        var (searchListener, searchBag) = SetupLongListener("pipeline.run.web_search_requests");
        var (billingListener, billingBag) = SetupDoubleListener("pipeline.run.billing_cost_usd");
        using var _ = tokenListener;
        using var __ = turnsListener;
        using var ___ = searchListener;
        using var ____ = billingListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["codegen"] = new()
                {
                    Tokens = 23315, Cost = 0.7371m, Provider = "claude", BillingMode = AgentBillingModes.Subscription,
                    InputTokens = 7771, OutputTokens = 7772, ReasoningTokens = 7773, CacheReadTokens = 7774,
                    CacheWriteTokens = 7775, Turns = 7731, WebSearchRequests = 7732
                }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        var tokensByType = tokenBag
            .Where(m => m.Value is >= 7771 and <= 7775 && Equals(m.Tags["provider"], "claude"))
            .ToDictionary(m => m.Tags["token_type"]!.ToString()!, m => m.Value);
        tokensByType.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["input"] = 7771, ["output"] = 7772, ["reasoning"] = 7773, ["cache_read"] = 7774, ["cache_write"] = 7775
        });
        turnsBag.Should().ContainSingle(m => m.Value == 7731).Which.Tags["provider"].Should().Be("claude");
        searchBag.Should().ContainSingle(m => m.Value == 7732);
        billingBag.Should().ContainSingle(m => Math.Abs(m.Value - 0.7371) < 1e-9)
            .Which.Tags["billing"].Should().Be("subscription");
    }

    [Fact]
    public async Task PhaseBreakdown_UnknownProvider_IsRecordedAsUnknown()
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
                ["analysis"] = new() { Tokens = 6191, Provider = "made-up-provider" }
            }
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        tokensBag.Should().ContainSingle(m => m.Value == 6191).Which.Tags["provider"].Should().Be("unknown");
    }

    [Fact]
    public async Task RateLimits_EmitEventCounterAndUtilizationHistogram()
    {
        var opts = CreateDbOptions();
        var item = await SeedRunningItemAsync(opts);
        var svc = CreateService(opts);

        var (eventsListener, eventsBag) = SetupLongListener("pipeline.run.rate_limit_events");
        var (utilizationListener, utilizationBag) = SetupDoubleListener("pipeline.run.rate_limit_utilization");
        using var _ = eventsListener;
        using var __ = utilizationListener;

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            RateLimits =
            [
                new AgentRateLimitObservation { Provider = "claude", Window = "seven_day_opus", Status = "allowed_warning", Utilization = 0.8317 },
                new AgentRateLimitObservation { Provider = "claude", Window = "brand_new_window", Status = "weird" }
            ]
        };

        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, Result = SerializePayload(payload) },
            CancellationToken.None, awaitTelemetry: true);

        eventsBag.Should().Contain(m => Equals(m.Tags["window"], "seven_day_opus") && Equals(m.Tags["status"], "allowed_warning"));
        eventsBag.Should().Contain(m => Equals(m.Tags["window"], "other") && Equals(m.Tags["status"], "other"),
            "unknown windows and statuses are folded into 'other' to bound label cardinality");
        utilizationBag.Should().ContainSingle(m => Math.Abs(m.Value - 0.8317) < 1e-9)
            .Which.Tags["window"].Should().Be("seven_day_opus");
        utilizationBag.Should().NotContain(m => Equals(m.Tags["window"], "other"),
            "a reading without utilization records no histogram sample");
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
