using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog.Events;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 req 9.1, 9.6, 9.10–9.13 (task 4.10): <c>pipeline.routing.refusals</c> is counted where a route is refused
/// (stage <c>create</c>, <c>dispatch</c>, <c>claim</c>) with the reason code; the creation and claim spans carry the
/// route attributes, and a refused claim the refusal attribute and the Error status; <c>pipeline.run.outcomes</c>
/// carries the route's tool and model; no log line carries a tool setting or an MCP env value. The meter is
/// process-wide, so the class runs alone (its collection disables parallelization) and each test listens only while
/// it runs.
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class RoutingTelemetryTests
{
    private const string SettingSecret = "setting-value-s3cr3t";
    private const string McpSecret = "mcp-env-value-s3cr3t";

    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());

    /// <summary>A profile whose tool setting and MCP env hold values no log line may show.</summary>
    private static readonly AgentProfile SecretHolder = AgentRoutingTestData.Profile(
        "profile-kiro",
        settings: new Dictionary<string, string> { [ProviderSettingKeys.AgentName] = SettingSecret },
        mcpServers: [new McpServerConfig { Name = "db", Env = new Dictionary<string, string> { ["DB_PASSWORD"] = McpSecret } }]);

    private static JobDistributionRequest Request(WorkItemTaskType taskType = WorkItemTaskType.Implementation) => new()
    {
        IssueIdentifier = new IssueIdentifier($"telemetry-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = taskType,
        TimeoutSeconds = 3600,
        PayloadSchemaVersion = 1,
    };

    // ── Listeners ────────────────────────────────────────────────────────────

    /// <summary>The measurements of one instrument of <paramref name="meterName"/> while it is alive.</summary>
    private sealed class InstrumentListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ConcurrentBag<Dictionary<string, string>> Measurements { get; } = [];

        public InstrumentListener(string instrumentName, string meterName = PipelineTelemetry.SourceName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (value == 0)
                    return;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags)
                    map[tag.Key] = tag.Value?.ToString() ?? "";
                Measurements.Add(map);
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A span of a test-only source, recorded, as <see cref="Activity.Current"/>.</summary>
    private sealed class RecordedSpan : IDisposable
    {
        private readonly ActivitySource _source = new($"routing-telemetry-{Guid.NewGuid():N}");
        private readonly ActivityListener _listener;

        public Activity Activity { get; }

        public RecordedSpan()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == _source.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
            Activity = _source.StartActivity("routing")!;
        }

        public void Dispose()
        {
            Activity.Dispose();
            _listener.Dispose();
            _source.Dispose();
        }
    }

    /// <summary>A Microsoft logger that keeps every rendered message and every structured value.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentBag<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : "";
            Lines.Add($"{formatter(state, exception)} {values}");
        }
    }

    // ── pipeline.routing.refusals ────────────────────────────────────────────

    [Fact]
    public async Task TheRouter_CountsACreateRefusal_WithTheReason_AndMarksTheSpan()
    {
        using var refusals = new InstrumentListener("pipeline.routing.refusals");
        using var span = new RecordedSpan();

        var result = await AgentRoutingTestSupport.Router(_db).RouteAsync(
            Request(WorkItemTaskType.Review), PipelineTelemetry.RoutingStages.Create, CancellationToken.None);

        result.IsRefused.Should().BeTrue();
        refusals.Measurements.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["task_type"] = nameof(WorkItemTaskType.Review),
            ["stage"] = "create",
            ["reason"] = AgentRouteRefusalReason.NoRoute,
        });
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.RouteRefusal).Should().Be(AgentRouteRefusalReason.NoRoute);
        span.Activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task TheRolloutRouting_CountsADispatchRefusal()
    {
        using var refusals = new InstrumentListener("pipeline.routing.refusals");
        using var attempts = new InstrumentListener("workdistribution.dispatch.attempts", WorkDistributionTelemetry.MeterName);
        var id = Guid.NewGuid();
        using (var db = _db.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = id, TaskType = WorkItemTaskType.Triage, IssueIdentifier = $"rollout-{id:N}", IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending, AgentSelector = "", TimeoutSeconds = 3600, CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.Serialize(Request(WorkItemTaskType.Triage), PipelineJsonOptions.Default),
            });
            db.SaveChanges();
        }
        var rollout = new RolloutRouting(_db, AgentRoutingTestSupport.Router(_db), new WorkItemStatusTransitionService(
            new WorkItemTransitionService(_db, NullLogger<WorkItemTransitionService>.Instance),
            new Mock<IRunLifecycleManager>().Object, NullLogger<WorkItemStatusTransitionService>.Instance, _db));

        await rollout.RouteAsync(id, CancellationToken.None);

        refusals.Measurements.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["task_type"] = nameof(WorkItemTaskType.Triage),
            ["stage"] = "dispatch",
            ["reason"] = AgentRouteRefusalReason.NoRoute,
        });
        attempts.Measurements.Should().ContainSingle(m => m["reason"] == "no_route", "req 9.12: the dispatch attempt names the refusal")
            .Which.Should().Contain("result", "deferred");
    }

    [Fact]
    public async Task TheEnricher_CountsAClaimRefusal_AndMarksTheSpan()
    {
        using var refusals = new InstrumentListener("pipeline.routing.refusals");
        using var span = new RecordedSpan();
        var enricher = Enricher(profiles: [], logger: Serilog.Log.Logger);
        var identity = Request() with
        {
            AgentSelector = AgentRoutingTestData.DefaultJobTemplate,
            Route = new AgentRouteSnapshot("gone", "Gone", JobTemplateProviderType.Kiro, "auto", "auto", AgentRouteSource.GlobalDefault),
        };

        var result = await enricher.EnrichAsync(identity, new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "P" }, CancellationToken.None);

        result.Should().BeOfType<EnrichmentResult.Refused>();
        refusals.Measurements.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["task_type"] = nameof(WorkItemTaskType.Implementation),
            ["stage"] = "claim",
            ["reason"] = AgentRouteRefusalReason.ProfileUnavailable,
        });
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.RouteRefusal).Should().Be(AgentRouteRefusalReason.ProfileUnavailable);
        span.Activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    // ── Route attributes ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheRouter_SetsTheRouteAttributesOnTheCreationSpan()
    {
        AgentRoutingTestSupport.SeedRouting(_db, AgentRoutingTestData.Routing(model: "opus", effort: "high"));
        using var span = new RecordedSpan();

        await AgentRoutingTestSupport.Router(_db).RouteAsync(Request(), PipelineTelemetry.RoutingStages.Create, CancellationToken.None);

        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.AgentProfile).Should().Be("profile-kiro");
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.Model).Should().Be("opus");
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.Effort).Should().Be("high");
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.RouteSource).Should().Be(AgentRouteLabels.Source(AgentRouteSource.GlobalDefault));
    }

    [Fact]
    public async Task TheEnricher_SetsTheRouteAttributesOnTheClaimSpan()
    {
        using var span = new RecordedSpan();
        var enricher = Enricher(profiles: [AgentRoutingTestData.Profile()], logger: Serilog.Log.Logger, throwFromCore: true);
        var identity = Request() with
        {
            AgentSelector = AgentRoutingTestData.DefaultJobTemplate,
            Route = new AgentRouteSnapshot("profile-kiro", "profile-kiro", JobTemplateProviderType.Kiro, "sonnet", "low", AgentRouteSource.Repository),
        };

        // The core preparation is stubbed to throw; the attributes are set when the route resolves, before it.
        var act = () => enricher.EnrichAsync(identity, new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "P" }, CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>();

        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.Model).Should().Be("sonnet");
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.Effort).Should().Be("low");
        span.Activity.GetTagItem(PipelineTelemetry.RoutingAttributes.RouteSource).Should().Be(AgentRouteLabels.Source(AgentRouteSource.Repository));
    }

    // ── No secret values in log lines (req 9.13) ─────────────────────────────

    [Fact]
    public async Task NoLogLine_CarriesAToolSettingOrAnMcpEnvValue()
    {
        AgentRoutingTestSupport.SeedRouting(_db);
        var routerLog = new CapturingLogger<WorkItemRouter>();
        var router = new WorkItemRouter(
            Profiles(SecretHolder), new AgentRoutingStore(_db), Templates(), AgentRoutingTestData.JobTemplates(), routerLog);
        var claimEvents = new List<LogEvent>();
        var enricher = Enricher(profiles: [SecretHolder], logger: AgentRoutingTestSupport.Logger(claimEvents));

        var routed = (await router.RouteAsync(Request(), PipelineTelemetry.RoutingStages.Create, CancellationToken.None)).Request!;
        await enricher.EnrichAsync(routed with { Route = null }, new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "P" }, CancellationToken.None);
        await enricher.EnrichAsync(routed with { Route = routed.Route! with { ProfileId = "gone" } },
            new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "P" }, CancellationToken.None);

        var lines = routerLog.Lines.Concat(claimEvents.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Values))).ToList();
        lines.Should().NotBeEmpty();
        lines.Should().NotContain(l => l.Contains(SettingSecret, StringComparison.Ordinal) || l.Contains(McpSecret, StringComparison.Ordinal));
    }

    // ── pipeline.run.outcomes: provider and model from the route (req 9.1) ───

    [Theory]
    [InlineData(true, JobTemplateProviderType.Claude, "claude-opus-4")]
    [InlineData(false, "unknown", "unknown")]
    public async Task RunOutcomes_CarryTheRoutesToolAndModel_OrUnknownWithoutARoute(bool withRoute, string provider, string model)
    {
        using var outcomes = new InstrumentListener("pipeline.run.outcomes");
        var request = Request() with
        {
            Route = withRoute
                ? new AgentRouteSnapshot("claude", "Claude", JobTemplateProviderType.Claude, "claude-opus-4", "high", AgentRouteSource.Repository)
                : null,
        };
        var id = Guid.NewGuid();
        using (var db = _db.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = id, TaskType = WorkItemTaskType.Implementation, IssueIdentifier = $"outcome-{id:N}", IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running, AgentSelector = "claude,dotnet", TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default),
            });
            db.SaveChanges();
        }
        var transitions = new WorkItemStatusTransitionService(
            new WorkItemTransitionService(_db, NullLogger<WorkItemTransitionService>.Instance),
            new Mock<IRunLifecycleManager>().Object, NullLogger<WorkItemStatusTransitionService>.Instance, _db);

        await transitions.TransitionAsync(id, new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded }, CancellationToken.None, awaitTelemetry: true);

        outcomes.Measurements.Should().ContainSingle().Which.Should().Contain(new Dictionary<string, string>
        {
            ["provider"] = provider,
            ["model"] = model,
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IAgentProfileStore Profiles(params AgentProfile[] profiles)
    {
        var store = new Mock<IAgentProfileStore>();
        store.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);
        return store.Object;
    }

    private static IProjectStore Templates()
    {
        var store = new Mock<IProjectStore>();
        store.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        store.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return store.Object;
    }

    /// <summary>A real enricher over the test routing table, with the dispatch preparation stubbed.</summary>
    private AssignmentEnricher Enricher(IReadOnlyList<AgentProfile> profiles, Serilog.ILogger logger, bool throwFromCore = false)
    {
        var projects = Templates();
        return new AssignmentEnricher(
            new StubInfrastructure(throwFromCore), Profiles([.. profiles]), Mock.Of<IConsolidationJobPreparationService>(), projects,
            new ConsolidationTemplateResolver(projects), new AgentRoutingStore(_db), AgentRoutingTestData.JobTemplates(), logger);
    }

    private sealed class StubInfrastructure(bool throwFromCore) : DispatchInfrastructure()
    {
        internal override Task<(IReadOnlyList<QualityGateConfiguration> QualityGates,
            IReadOnlyList<ReviewerConfiguration> Reviewers,
            IssueContextResult IssueContext,
            IReadOnlyList<ProviderConfig> ProviderConfigs,
            PipelineConfiguration Config,
            bool ForceRefresh,
            string? StalenessSignal,
            int RefreshCount)?> PrepareDispatchCoreAsync(DispatchCoreRequest request, CancellationToken ct)
        {
            if (throwFromCore)
                throw new TimeoutException("DB timeout");
            var context = new IssueContextResult(
                new IssueDetail { Identifier = "1", Title = "t", Description = "d", Labels = [] },
                new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" }, [], null, false, null, 0);
            return Task.FromResult<(IReadOnlyList<QualityGateConfiguration>, IReadOnlyList<ReviewerConfiguration>, IssueContextResult,
                IReadOnlyList<ProviderConfig>, PipelineConfiguration, bool, string?, int)?>(
                ([], [], context, [request.AgentConfig!], new PipelineConfiguration(), false, null, 0));
        }
    }
}
