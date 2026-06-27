// Feature: 035a-postgres-work-queue
// Property 14: Cache Coherence
using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 14: Cache Coherence
/// Write config via SavePipelineConfigAsync, immediately read via LoadPipelineConfigAsync,
/// assert values match. Verifies that the in-memory cache is properly invalidated on write.
/// **Validates: Requirements 3.5**
/// </summary>
public class CacheCoherencePropertyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .Build();

    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    private PipelineDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new PipelineDbContext(options);
    }

    private PostgresConfigurationStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new PostgresConfigurationStore(factory);
    }

    /// <summary>
    /// Property 14: Cache Coherence — write then immediate read returns same config.
    /// For any valid PipelineConfiguration, SavePipelineConfigAsync followed by
    /// LoadPipelineConfigAsync SHALL return a configuration with matching field values.
    /// **Validates: Requirements 3.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(CacheCoherenceArbitraries) })]
    public async Task SaveThenLoad_ReturnsSameConfiguration(PipelineConfigTestData testData)
    {
        // Each iteration gets a fresh store to avoid cross-iteration cache interference
        var store = CreateStore();
        var config = testData.Config;

        await store.SavePipelineConfigAsync(config, CancellationToken.None);
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        // Verify key fields match (JSON round-trip equivalence)
        Assert.Equal(config.MaxRetries, loaded.MaxRetries);
        Assert.Equal(config.MaxAnalysisRetries, loaded.MaxAnalysisRetries);
        Assert.Equal(config.IssuePageSize, loaded.IssuePageSize);
        Assert.Equal(config.AnalysisReviewEnabled, loaded.AnalysisReviewEnabled);
        Assert.Equal(config.AcceptanceCriteriaEnabled, loaded.AcceptanceCriteriaEnabled);
        Assert.Equal(config.BaselineHealthCheckEnabled, loaded.BaselineHealthCheckEnabled);
        Assert.Equal(config.RefactoringReviewEnabled, loaded.RefactoringReviewEnabled);
        Assert.Equal(config.BrainConsolidationReviewEnabled, loaded.BrainConsolidationReviewEnabled);
        Assert.Equal(config.HarnessSuggestionsReviewEnabled, loaded.HarnessSuggestionsReviewEnabled);
        Assert.Equal(config.AnalysisPrompt, loaded.AnalysisPrompt);
        Assert.Equal(config.ImplementationPrompt, loaded.ImplementationPrompt);
        Assert.Equal(config.BlacklistedPaths, loaded.BlacklistedPaths);

        // Also verify via full JSON serialization equivalence
        var originalJson = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        var loadedJson = JsonSerializer.Serialize(loaded, PipelineJsonOptions.Default);
        Assert.Equal(originalJson, loadedJson);
    }

    /// <summary>
    /// Complementary: writing a second config overwrites the cache — load returns the latest.
    /// Verifies cache is invalidated on every write, not just the first.
    /// **Validates: Requirements 3.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(CacheCoherenceArbitraries) })]
    public async Task OverwriteThenLoad_ReturnsLatestConfiguration(
        PipelineConfigTestData first,
        PipelineConfigTestData second)
    {
        var store = CreateStore();

        // Write first config
        await store.SavePipelineConfigAsync(first.Config, CancellationToken.None);

        // Overwrite with second config
        await store.SavePipelineConfigAsync(second.Config, CancellationToken.None);

        // Load should return the second config
        var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

        var expectedJson = JsonSerializer.Serialize(second.Config, PipelineJsonOptions.Default);
        var loadedJson = JsonSerializer.Serialize(loaded, PipelineJsonOptions.Default);
        Assert.Equal(expectedJson, loadedJson);
    }

    /// <summary>
    /// Simple IDbContextFactory implementation for tests.
    /// </summary>
    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options)
        {
            _options = options;
        }

        public PipelineDbContext CreateDbContext() => new(_options);
    }
}

/// <summary>
/// Wrapper record for FsCheck to generate PipelineConfiguration instances.
/// </summary>
public record PipelineConfigTestData(PipelineConfiguration Config);

/// <summary>
/// FsCheck arbitrary generators for Cache Coherence property tests.
/// Generates PipelineConfiguration with varied field values.
/// </summary>
public class CacheCoherenceArbitraries
{
    private static readonly string[] PromptPool =
    [
        "Analyze the code",
        "Fix the bug",
        "Review for security issues",
        "Implement feature",
        "Run tests"
    ];

    private static readonly string[] PathPool =
    [
        ".env",
        "secrets/",
        "node_modules/",
        ".git/",
        "bin/"
    ];

    public static Arbitrary<PipelineConfigTestData> PipelineConfigTestDataArb()
    {
        var gen =
            from config in GenPipelineConfiguration()
            select new PipelineConfigTestData(config);
        return gen.ToArbitrary();
    }

    private static Gen<PipelineConfiguration> GenPipelineConfiguration()
    {
        return
            from maxRetries in Gen.Choose(0, 10)
            from maxAnalysisRetries in Gen.Choose(0, 5)
            from issuePageSize in Gen.Choose(1, 100)
            from boolBits in Gen.Choose(0, 63)
            from analysisPromptIdx in Gen.Choose(0, PromptPool.Length - 1)
            from implementationPromptIdx in Gen.Choose(0, PromptPool.Length - 1)
            from pathMask in Gen.Choose(0, 31)
            select new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                MaxAnalysisRetries = maxAnalysisRetries,
                IssuePageSize = issuePageSize,
                AnalysisReviewEnabled = (boolBits & 1) != 0,
                AcceptanceCriteriaEnabled = (boolBits & 2) != 0,
                BaselineHealthCheckEnabled = (boolBits & 4) != 0,
                RefactoringReviewEnabled = (boolBits & 8) != 0,
                BrainConsolidationReviewEnabled = (boolBits & 16) != 0,
                HarnessSuggestionsReviewEnabled = (boolBits & 32) != 0,
                AnalysisPrompt = PromptPool[analysisPromptIdx],
                ImplementationPrompt = PromptPool[implementationPromptIdx],
                BlacklistedPaths = PathPool
                    .Where((_, i) => (pathMask & (1 << i)) != 0)
                    .ToList()
            };
    }
}
