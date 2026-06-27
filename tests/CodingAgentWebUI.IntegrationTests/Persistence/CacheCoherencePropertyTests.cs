// Feature: Persistence Integration Tests
// Property 14: Cache Coherence — Write-then-read produces identical values
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 14: Cache Coherence.
/// For any generated PipelineConfiguration, writing via PostgresConfigurationStore
/// and immediately reading back produces identical values.
/// Uses InMemory EF Core provider.
/// </summary>
public class CacheCoherencePropertyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;

    public CacheCoherencePropertyTests()
    {
        var dbName = $"CacheCoherence-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    /// <summary>
    /// Property 14: Cache Coherence.
    /// For any random PipelineConfiguration, saving via the store and loading it back
    /// yields semantically equivalent values (MaxRetries, AgentTimeout, IssuePageSize).
    /// </summary>
    [Property(Arbitrary = new[] { typeof(PipelineConfigArbitraries) })]
    public async Task<bool> SaveThenLoad_ProducesSameValues(PipelineConfiguration config)
    {
        // Use a fresh store each iteration to avoid cache hits
        var store = new PostgresConfigurationStore(_dbFactory, cacheTtl: TimeSpan.Zero);

        await store.SavePipelineConfigAsync(config, CancellationToken.None);

        // Create a second store to bypass in-memory cache
        var freshStore = new PostgresConfigurationStore(_dbFactory, cacheTtl: TimeSpan.Zero);
        var loaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);

        // Verify key properties survive round-trip through JSON serialization
        return loaded.MaxRetries == config.MaxRetries
            && loaded.AgentTimeout == config.AgentTimeout
            && loaded.IssuePageSize == config.IssuePageSize
            && loaded.AnalysisReviewEnabled == config.AnalysisReviewEnabled
            && loaded.AcceptanceCriteriaEnabled == config.AcceptanceCriteriaEnabled
            && loaded.BaselineHealthCheckEnabled == config.BaselineHealthCheckEnabled;
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var jsonConverter = new ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default));

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(jsonConverter);
                        property.SetColumnType(null);
                    }
                }

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
                {
                    entityType.RemoveIndex(index);
                }
            }
        }
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}

/// <summary>
/// FsCheck arbitrary generators for PipelineConfiguration (Property 14).
/// Generates configs with randomized MaxRetries, AgentTimeout, IssuePageSize, and boolean flags.
/// </summary>
public class PipelineConfigArbitraries
{
    public static Arbitrary<PipelineConfiguration> PipelineConfigArb()
    {
        var gen = from maxRetries in Gen.Choose(0, 10)
                  from timeoutMinutes in Gen.Choose(1, 120)
                  from pageSize in Gen.Choose(1, 100)
                  from analysisReview in Gen.Elements(true, false)
                  from acceptanceCriteria in Gen.Elements(true, false)
                  from baselineHealth in Gen.Elements(true, false)
                  select new PipelineConfiguration
                  {
                      MaxRetries = maxRetries,
                      AgentTimeout = TimeSpan.FromMinutes(timeoutMinutes),
                      IssuePageSize = pageSize,
                      AnalysisReviewEnabled = analysisReview,
                      AcceptanceCriteriaEnabled = acceptanceCriteria,
                      BaselineHealthCheckEnabled = baselineHealth
                  };
        return gen.ToArbitrary();
    }
}
