using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// xUnit collection for the ConcurrencyConflict priority-weight tests. Ensures a single
/// <see cref="ConcurrencyConflictApiFactory"/> instance is shared across all tests in
/// <see cref="PostPriorityConcurrencyTests"/>. <c>DisableParallelization = true</c>
/// prevents the "logger is already frozen" Serilog race that occurs when two
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// instances start concurrently — the same fix applied by <c>AgentHubGateCollection</c>.
/// </summary>
[CollectionDefinition("PostPriorityConcurrencyCollection", DisableParallelization = true)]
public sealed class PostPriorityConcurrencyCollection : ICollectionFixture<ConcurrencyConflictApiFactory> { }

/// <summary>
/// Integration tests for the ConcurrencyConflict 409 path of POST /api/work-items/{id}/priority.
///
/// Uses its own <see cref="ConcurrencyConflictApiFactory"/> (not the shared
/// <see cref="ApiIntegrationTestCollection"/> fixture) so the throwing
/// <see cref="WorkItemTransitionService"/> does not leak into the priority tests
/// that expect successful 200 responses.
/// </summary>
[Collection("PostPriorityConcurrencyCollection")]
public sealed class PostPriorityConcurrencyTests
{
    private readonly ConcurrencyConflictApiFactory _factory;
    private readonly HttpClient _client;

    public PostPriorityConcurrencyTests(ConcurrencyConflictApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority when UpdatePriorityWeightAsync exhausts all retries
    /// due to concurrent saves returns HTTP 409 with a body containing "concurrent update conflict".
    /// </summary>
    [Fact]
    public async Task PostPriority_WhenConcurrencyConflict_Returns409()
    {
        // Arrange: seed a Pending work item into the throwing factory's InMemory database.
        // Must use CreateThrowingDbContext() — the base factory's _dbName is private and the throwing
        // WorkItemTransitionService uses ThrowingDbName, not the base factory's database.
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-concurrency",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var db = _factory.CreateThrowingDbContext())
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(entity);
            db.SaveChanges();
        }

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = 500 },
            PipelineJsonOptions.Default);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("concurrent update conflict",
            "the ConcurrencyConflict result must be mapped to 409 with the conflict message, " +
            "distinct from the NotPending 409 which says 'not in Pending status'");
    }
}

/// <summary>
/// Derived <see cref="ApiWebApplicationFactory"/> that replaces
/// <see cref="WorkItemTransitionService"/> with an instance backed by
/// <see cref="ThrowingDbContextFactory"/>. Every call to
/// <c>SaveChangesAsync</c> on the produced contexts throws
/// <see cref="DbUpdateConcurrencyException"/>, causing
/// <c>UpdatePriorityWeightAsync</c> to exhaust all retries and return
/// <see cref="UpdatePriorityWeightResult.ConcurrencyConflict"/>.
///
/// Uses its own InMemory database (<see cref="ThrowingDbName"/>) so the throwing
/// behaviour is isolated from the shared collection fixture's database.
/// </summary>
public sealed class ConcurrencyConflictApiFactory : ApiWebApplicationFactory
{
    /// <summary>
    /// InMemory database name used by the <see cref="ThrowingDbContextFactory"/> and
    /// <see cref="CreateThrowingDbContext"/>. Separate from the base factory's private
    /// <c>_dbName</c> to guarantee isolation.
    /// </summary>
    public string ThrowingDbName { get; } = $"ConcurrencyConflictTest-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a normal (non-throwing) <see cref="PipelineDbContext"/> backed by
    /// <see cref="ThrowingDbName"/> for seeding test data.
    /// The <see cref="WorkItemTransitionService"/> replacement reads from this same database.
    /// </summary>
    public PipelineDbContext CreateThrowingDbContext()
    {
        var options = BuildThrowingDbOptions();
        return new NormalThrowingDbPipelineDbContext(options);
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        // Replace WorkItemTransitionService with one whose every SaveChangesAsync throws.
        // All four DI consumers of WorkItemTransitionService (IWorkItemFallbackTransitionService,
        // IWorkItemTransitionStore, WorkItemStatusTransitionService, DispatchLifecycleService) will
        // resolve the throwing instance, but none are in the POST /{id}/priority call path.
        services.RemoveAll<WorkItemTransitionService>();
        var throwingDbName = ThrowingDbName;
        services.AddSingleton<WorkItemTransitionService>(sp =>
        {
            var throwingFactory = new ThrowingDbContextFactory(throwingDbName);
            var logger = sp.GetRequiredService<ILoggerFactory>()
                           .CreateLogger<WorkItemTransitionService>();
            return new WorkItemTransitionService(throwingFactory, logger);
        });
    }

    private DbContextOptions<PipelineDbContext> BuildThrowingDbOptions() =>
        new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(ThrowingDbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    // ── InMemory DbContext subclasses ──────────────────────────────────────────

    /// <summary>
    /// Normal InMemory context used for seeding test data into <see cref="ThrowingDbName"/>.
    /// Disables RowVersion concurrency tokens and filtered indexes for InMemory compatibility.
    /// </summary>
    internal sealed class NormalThrowingDbPipelineDbContext : PipelineDbContext
    {
        public NormalThrowingDbPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DisableInMemoryIncompatibleFeatures(modelBuilder);
        }
    }

    /// <summary>
    /// InMemory context whose <see cref="SaveChangesAsync"/> always throws
    /// <see cref="DbUpdateConcurrencyException"/>. Reads function normally so
    /// <c>UpdatePriorityWeightAsync</c> finds the seeded item on each retry attempt
    /// before failing at the save step.
    /// </summary>
    private sealed class ThrowingOnSavePipelineDbContext : PipelineDbContext
    {
        public ThrowingOnSavePipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DisableInMemoryIncompatibleFeatures(modelBuilder);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException(
                "Simulated concurrency conflict for PostPriority_WhenConcurrencyConflict_Returns409");
    }

    // ── Factory ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> that always returns
    /// <see cref="ThrowingOnSavePipelineDbContext"/> instances backed by
    /// <paramref name="dbName"/>. Used to inject the throwing behaviour into
    /// <see cref="WorkItemTransitionService"/> without subclassing the sealed service.
    /// </summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;

        public ThrowingDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new ThrowingOnSavePipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    // ── Shared model configuration helper ─────────────────────────────────────

    /// <summary>
    /// Disables RowVersion concurrency tokens and filtered unique indexes that are
    /// incompatible with the InMemory EF Core provider.
    /// Mirrors the logic in the private <c>TestPipelineDbContext</c> inside
    /// <see cref="ApiWebApplicationFactory"/>.
    /// </summary>
    private static void DisableInMemoryIncompatibleFeatures(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var rowVersionProp = entityType.FindProperty("RowVersion");
            if (rowVersionProp != null)
            {
                rowVersionProp.IsConcurrencyToken = false;
                rowVersionProp.ValueGenerated = ValueGenerated.Never;
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
