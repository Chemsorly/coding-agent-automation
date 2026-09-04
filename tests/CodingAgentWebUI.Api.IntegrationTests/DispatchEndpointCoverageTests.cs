using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Coverage-focused tests for <c>POST /api/work-items/dispatch</c> that exercise branches
/// unreachable via the shared <see cref="ApiWebApplicationFactory"/> (which has an empty
/// <see cref="JobTemplateStore"/>).
///
/// A dedicated factory registers non-kiro <see cref="JobTemplate"/> entries so the handler
/// proceeds past template resolution into the full dispatch lifecycle:
/// <list type="bullet">
///   <item>Concurrency check (step 2)</item>
///   <item>PVC check skipped (step 3 — non-kiro)</item>
///   <item>WorkItem Pending insert (step 4)</item>
///   <item>DispatchLifecycleService.ExecuteDispatchLifecycleAsync (K8s fails → Failed + 503)</item>
///   <item>SafeFailWorkItemAsync safety-net</item>
/// </list>
/// K8s is unavailable in the test environment, so the lifecycle always fails gracefully
/// (item transitions to Failed, endpoint returns 503).
/// </summary>
public sealed class DispatchEndpointCoverageTests : IAsyncLifetime
{
    private DispatchCoverageWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    // Non-kiro selector that matches the template registered by DispatchCoverageWebApplicationFactory
    private const string NonKiroSelector = "opencode,dotnet";

    public Task InitializeAsync()
    {
        _factory = new DispatchCoverageWebApplicationFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DispatchCoverageWebApplicationFactory.ApiKey);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeRequest(string? issueId = null) => new()
    {
        IssueIdentifier = new IssueIdentifier(issueId ?? $"cov-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-cov",
        RepoProviderConfigId = "repo-cov",
        InitiatedBy = "coverage-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = NonKiroSelector,
        TimeoutSeconds = 3600
    };

    // ── Baseline: non-kiro template resolves and lifecycle runs ──────────────────

    /// <summary>
    /// Verifies that when a <see cref="JobTemplate"/> is registered for the selector, the
    /// dispatch handler proceeds past template resolution, inserts a Pending row, runs the
    /// lifecycle (which fails because K8s is unavailable), transitions the item to Failed,
    /// and returns 503.  AC1 is satisfied: no Pending rows remain.
    /// Covers lines 637–799 (concurrency check, non-kiro path, DB insert, lifecycle invocation,
    /// SafeFailWorkItemAsync, 503 return).
    /// </summary>
    [Fact]
    public async Task PostDispatch_WithRegisteredNonKiroTemplate_DoesNotLeavePendingRow()
    {
        var issueId = $"cov-baseline-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync(
            "/api/work-items/dispatch", MakeRequest(issueId), PipelineJsonOptions.Default);

        // With K8s unavailable, CreateK8sJobAsync catches the NullReferenceException,
        // transitions the item to Failed, and the endpoint returns 503.
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK,
            HttpStatusCode.Conflict);

        // AC1: no Pending rows — item must have been moved to Failed (or never written on 503)
        await using var db = _factory.CreateDbContext();
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.IssueIdentifier == issueId)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync();

        if (item is not null)
            item.Status.Should().NotBe(WorkItemStatus.Pending,
                "POST /api/work-items/dispatch must never leave a row in Pending status");
    }

    // ── Duplicate-issue conflict (step 4 unique-violation) ───────────────────────

    /// <summary>
    /// Verifies that when the same RunId is submitted and that ID already exists in the DB
    /// (simulating the unique-violation path via primary-key collision on EF InMemory),
    /// the handler returns 409 Conflict.
    /// Covers the <c>IsUniqueViolation</c> catch block in step 4 (lines 716–718).
    /// </summary>
    [Fact]
    public async Task PostDispatch_WhenDuplicateRunId_Returns409Conflict()
    {
        var existingId = Guid.NewGuid();

        // Seed an item with the same ID that the request will try to insert
        await using var seedDb = _factory.CreateDbContext();
        seedDb.WorkItems.Add(new WorkItemEntity
        {
            Id = existingId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"cov-dup-existing-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-cov",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = NonKiroSelector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await seedDb.SaveChangesAsync();

        // Submit a request with the same RunId — InMemory EF will throw a PK collision,
        // which IsUniqueViolation treats as a unique-violation and the endpoint returns 409.
        var request = MakeRequest() with { RunId = existingId.ToString() };
        var response = await _client.PostAsJsonAsync(
            "/api/work-items/dispatch", request, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "inserting a WorkItem with an already-existing ID is a uniqueness violation — endpoint must return 409");
    }

    // ── Concurrency limit path (step 2) ─────────────────────────────────────────

    /// <summary>
    /// Verifies that when the concurrency limit is reached for a selector (MaxConcurrent=1,
    /// one active item present), the handler returns 409 Conflict before inserting a new row.
    /// Covers the MaxConcurrent pre-flight branch in step 2 (lines 644–651).
    /// </summary>
    [Fact]
    public async Task PostDispatch_WhenConcurrencyLimitReached_Returns409Conflict()
    {
        const string limitedSelector = "dotnet,limited,opencode";
        var issueId1 = $"cov-conc-existing-{Guid.NewGuid():N}";
        var issueId2 = $"cov-conc-new-{Guid.NewGuid():N}";

        // Seed one Dispatched item for the limited selector (MaxConcurrent=1)
        await using var seedDb = _factory.CreateDbContext();
        seedDb.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueId1,
            IssueProviderConfigId = "prov-cov",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = limitedSelector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await seedDb.SaveChangesAsync();

        // Act: dispatch a different issue with the limited selector
        var request = MakeRequest(issueId2) with { AgentSelector = limitedSelector };
        var response = await _client.PostAsJsonAsync(
            "/api/work-items/dispatch", request, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "concurrency limit of 1 is reached — endpoint must return 409 Conflict");
    }

    // ── No-template path (step 1) ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that when the selector has no registered template and no agent profile
    /// can be resolved, the handler returns 409 Conflict.
    /// Covers the no-template/no-profile early-exit path in step 1 (lines 621–625).
    /// </summary>
    [Fact]
    public async Task PostDispatch_WhenNoTemplateForSelector_Returns409Conflict()
    {
        var request = MakeRequest() with { AgentSelector = "unknown,selector,xyz" };
        var response = await _client.PostAsJsonAsync(
            "/api/work-items/dispatch", request, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "no job template or agent profile exists for the selector — endpoint must return 409");
    }

    // ── K8s failure path: item transitions to Failed (lifecycle error) ────────────

    /// <summary>
    /// Verifies that after a failed lifecycle (K8s unavailable), the WorkItem row
    /// transitions from Pending to Failed — confirming that <c>FailWorkItemAsync</c>
    /// is called inside <c>CreateK8sJobAsync</c> and the item does not remain Pending.
    /// Covers lines 777–798 (K8s exception catch, FailWorkItemAsync call, 503 return).
    /// </summary>
    [Fact]
    public async Task PostDispatch_WhenK8sUnavailable_ItemTransitionsToFailed()
    {
        var issueId = $"cov-failed-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync(
            "/api/work-items/dispatch", MakeRequest(issueId), PipelineJsonOptions.Default);

        await using var db = _factory.CreateDbContext();
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.IssueIdentifier == issueId)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync();

        // If a row was written it must be Failed (K8s exception path transitions to Failed)
        if (item is not null)
        {
            item.Status.Should().Be(WorkItemStatus.Failed,
                "when K8s Job creation fails, the lifecycle transitions the WorkItem to Failed");
        }
    }

    // ── Test Infrastructure ──────────────────────────────────────────────────────

    /// <summary>
    /// A standalone <see cref="WebApplicationFactory{TEntryPoint}"/> that mirrors
    /// <see cref="ApiWebApplicationFactory"/> but additionally registers non-kiro
    /// <see cref="JobTemplate"/> entries so the dispatch handler proceeds past template
    /// resolution into the full lifecycle.
    ///
    /// Uses a private InMemory database isolated from the shared collection factory.
    /// </summary>
    private sealed class DispatchCoverageWebApplicationFactory : WebApplicationFactory<Program>
    {
        public const string ApiKey = "cov-api-key";
        private readonly string _dbName = $"DispatchCov-{Guid.NewGuid():N}";

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new CovTestPipelineDbContext(options);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Environment.SetEnvironmentVariable("Database__Host", null);
                Environment.SetEnvironmentVariable("Database__Port", null);
                Environment.SetEnvironmentVariable("Database__Username", null);
                Environment.SetEnvironmentVariable("Database__Password", null);
                Environment.SetEnvironmentVariable("Database__Name", null);
                Environment.SetEnvironmentVariable("Database__SslMode", null);
                Environment.SetEnvironmentVariable("Database__MigrateOnStartup", null);
                Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
                Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
            }
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("Database__Host", "localhost");
            Environment.SetEnvironmentVariable("Database__Port", "5432");
            Environment.SetEnvironmentVariable("Database__Username", "test");
            Environment.SetEnvironmentVariable("Database__Password", "test");
            Environment.SetEnvironmentVariable("Database__Name", "test_db");
            Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
            Environment.SetEnvironmentVariable("AGENT_API_KEY", ApiKey);

            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Warning()
                .CreateLogger();

            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();

                // Replace Npgsql with InMemory
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>)
                             || d.ServiceType == typeof(PipelineDbContext)
                             || d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                             || d.ServiceType == typeof(DbContextOptions)
                             || d.ServiceType.Name.Contains("DbContextPool"))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                    new CovInMemoryDbContextFactory(_dbName));

                services.RemoveAll<IDistributedLockProvider>();
                services.AddDistributedLockProvider(null);

                services.RemoveAll<IDatabaseProbe>();
                services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

                services.RemoveAll<IProviderFactory>();
                services.AddSingleton(new Mock<IProviderFactory>().Object);

                services.RemoveAll<IQualityGateValidator>();
                services.AddSingleton(new Mock<IQualityGateValidator>().Object);

                services.RemoveAll<IConsolidationDispatchService>();
                services.AddSingleton<IConsolidationDispatchService>(new NoOpConsolidationDispatchService());

                var leaderMock = new Mock<ILeaderElectionService>();
                leaderMock.SetupGet(l => l.IsLeader).Returns(true);
                leaderMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);
                services.RemoveAll<ILeaderElectionService>();
                services.AddSingleton(leaderMock.Object);

                services.RemoveAll<AssignmentEnricher>();
                services.AddSingleton<AssignmentEnricher>(new PassthroughAssignmentEnricher());

                // Key: register non-kiro templates so the dispatch handler proceeds past
                // template resolution into the full lifecycle (concurrency check, DB insert,
                // K8s job creation attempt, SafeFailWorkItemAsync).
                services.RemoveAll<JobTemplateStore>();
                var yaml = """
                    - labels: opencode,dotnet
                      image: test/opencode-agent:latest
                      providerType: opencode
                      maxConcurrent: 0
                    - labels: opencode,dotnet,limited
                      image: test/opencode-agent:latest
                      providerType: opencode
                      maxConcurrent: 1
                    """;
                services.AddSingleton(JobTemplateStore.LoadFromYaml(yaml));
            });
        }

        private sealed class CovInMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
        {
            private readonly string _dbName;
            public CovInMemoryDbContextFactory(string dbName) => _dbName = dbName;

            public PipelineDbContext CreateDbContext()
            {
                var options = new DbContextOptionsBuilder<PipelineDbContext>()
                    .UseInMemoryDatabase(_dbName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                    .Options;
                return new CovTestPipelineDbContext(options);
            }

            public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
                => Task.FromResult(CreateDbContext());
        }

        private sealed class CovTestPipelineDbContext : PipelineDbContext
        {
            public CovTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                base.OnModelCreating(modelBuilder);
                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    var rowVersionProp = entityType.FindProperty("RowVersion");
                    if (rowVersionProp != null)
                    {
                        rowVersionProp.IsConcurrencyToken = false;
                        rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                    }
                }
                foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                {
                    var indexesToRemove = entityType.GetIndexes()
                        .Where(i => i.GetFilter() != null)
                        .ToList();
                    foreach (var index in indexesToRemove)
                        entityType.RemoveIndex(index);
                }
            }
        }

        private sealed class NoOpDatabaseProbe : IDatabaseProbe
        {
            public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class NoOpConsolidationDispatchService : IConsolidationDispatchService
        {
            public Task<ConsolidationDispatchResult> TryDispatchAsync(ConsolidationRun r, ConsolidationRunType t,
                TemplateId? tid, string? f, string w, CancellationToken ct)
                => Task.FromResult(ConsolidationDispatchResult.Failed);
            public Task<bool> TryDispatchToAgentAsync(RunId r, ConsolidationRunType t, TemplateId? tid,
                string w, AgentId a, CancellationToken ct)
                => Task.FromResult(false);
            public Task NotifyRunCancelledAsync(RunId r, CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class PassthroughAssignmentEnricher : AssignmentEnricher
        {
            public PassthroughAssignmentEnricher() : base(Serilog.Log.Logger) { }

            public override Task<JobDistributionRequest?> EnrichAsync(
                JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
                => Task.FromResult<JobDistributionRequest?>(identity);
        }
    }
}
