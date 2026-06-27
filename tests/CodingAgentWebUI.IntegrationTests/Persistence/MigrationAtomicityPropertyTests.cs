// Feature: 035a-postgres-work-queue
// Property 7: Migration Atomicity
using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property 7: Migration Atomicity
/// For any set of configuration files where at least one file contains invalid JSON,
/// running migration SHALL leave the database completely empty (transaction rolled back).
/// No partial state.
/// **Validates: Requirements 2.5**
/// </summary>
public class MigrationAtomicityPropertyTests : IAsyncLifetime
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

    private IDbContextFactory<PipelineDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>
    /// Property 7: Migration Atomicity — invalid JSON in any config file slot causes full rollback.
    /// For each iteration:
    /// 1. Generate valid config files (pipeline-config, providers, profiles, etc.)
    /// 2. Inject ONE invalid/corrupt JSON file in a random location
    /// 3. Run migration → expect it to throw
    /// 4. Assert: DB is completely empty (transaction was rolled back, no partial state)
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MigrationAtomicityArbitraries) })]
    public async Task InvalidJsonFile_CausesFullRollback_DbRemainsEmpty(MigrationAtomicityInput input)
    {
        // Ensure DB is clean before each iteration
        await using (var cleanCtx = CreateContext())
        {
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"PipelineConfig\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"ProviderConfigs\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"AgentProfiles\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"QualityGateConfigs\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"ReviewerConfigs\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"Projects\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"PipelineJobTemplates\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"ConsolidationRuns\"");
            await cleanCtx.Database.ExecuteSqlRawAsync("DELETE FROM \"PipelineRuns\"");
        }

        // Create temp directory with config files
        var tempDir = Path.Combine(Path.GetTempPath(), $"migration-atomicity-{Guid.NewGuid():N}");
        try
        {
            WriteConfigFiles(tempDir, input);

            var factory = CreateFactory();
            var lockProvider = new TestDistributedLockProvider();
            var service = new ConfigMigrationService(factory, lockProvider, tempDir);

            // Migration should throw due to invalid JSON
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.MigrateIfNeededAsync(CancellationToken.None));

            // Assert DB is completely empty — no partial state
            await using var ctx = CreateContext();
            Assert.False(await ctx.PipelineConfig.AnyAsync());
            Assert.False(await ctx.ProviderConfigs.AnyAsync());
            Assert.False(await ctx.AgentProfiles.AnyAsync());
            Assert.False(await ctx.QualityGateConfigs.AnyAsync());
            Assert.False(await ctx.ReviewerConfigs.AnyAsync());
            Assert.False(await ctx.Projects.AnyAsync());
            Assert.False(await ctx.PipelineJobTemplates.AnyAsync());
            Assert.False(await ctx.ConsolidationRuns.AnyAsync());
            Assert.False(await ctx.PipelineRuns.AnyAsync());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteConfigFiles(string basePath, MigrationAtomicityInput input)
    {
        Directory.CreateDirectory(basePath);
        var opts = PipelineJsonOptions.Default;

        // 1. Pipeline config
        var pipelineConfigPath = Path.Combine(basePath, "pipeline-config.json");
        if (input.CorruptSlot == ConfigSlot.PipelineConfig)
        {
            File.WriteAllText(pipelineConfigPath, input.InvalidJson);
        }
        else
        {
            var config = new PipelineConfiguration { MaxRetries = input.MaxRetries };
            File.WriteAllText(pipelineConfigPath, JsonSerializer.Serialize(config, opts));
        }

        // 2. Provider configs
        var providerDir = Path.Combine(basePath, "providers", "issue");
        Directory.CreateDirectory(providerDir);
        var providerPath = Path.Combine(providerDir, $"{Guid.NewGuid()}.json");
        if (input.CorruptSlot == ConfigSlot.ProviderConfig)
        {
            File.WriteAllText(providerPath, input.InvalidJson);
        }
        else
        {
            var provider = new ProviderConfig
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = input.ProviderDisplayName,
                Kind = ProviderKind.Issue,
                ProviderType = "GitHub"
            };
            File.WriteAllText(providerPath, JsonSerializer.Serialize(provider, opts));
        }

        // 3. Agent profiles
        var profilesDir = Path.Combine(basePath, "profiles");
        Directory.CreateDirectory(profilesDir);
        var profilePath = Path.Combine(profilesDir, $"{Guid.NewGuid()}.json");
        if (input.CorruptSlot == ConfigSlot.AgentProfile)
        {
            File.WriteAllText(profilePath, input.InvalidJson);
        }
        else
        {
            var profile = new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = input.ProfileDisplayName,
                AgentProviderConfigId = Guid.NewGuid().ToString()
            };
            File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, opts));
        }

        // 4. Quality gates
        var qgDir = Path.Combine(basePath, "quality-gates");
        Directory.CreateDirectory(qgDir);
        var qgPath = Path.Combine(qgDir, $"{Guid.NewGuid()}.json");
        if (input.CorruptSlot == ConfigSlot.QualityGate)
        {
            File.WriteAllText(qgPath, input.InvalidJson);
        }
        else
        {
            var qg = new QualityGateConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = input.QualityGateDisplayName
            };
            File.WriteAllText(qgPath, JsonSerializer.Serialize(qg, opts));
        }

        // 5. Reviewers
        var reviewersDir = Path.Combine(basePath, "reviewers");
        Directory.CreateDirectory(reviewersDir);
        var reviewerPath = Path.Combine(reviewersDir, $"{Guid.NewGuid()}.json");
        if (input.CorruptSlot == ConfigSlot.ReviewerConfig)
        {
            File.WriteAllText(reviewerPath, input.InvalidJson);
        }
        else
        {
            var reviewer = new ReviewerConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = input.ReviewerDisplayName,
                Agents = [new ReviewAgent { Name = "TestReviewer", Prompt = "Review the code" }]
            };
            File.WriteAllText(reviewerPath, JsonSerializer.Serialize(reviewer, opts));
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Simple in-process lock provider for test isolation.
    /// </summary>
    private sealed class TestDistributedLockProvider : IDistributedLockProvider
    {
        public Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new NoOpHandle());

        private sealed class NoOpHandle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Identifies which config file slot gets the invalid JSON injection.
/// </summary>
public enum ConfigSlot
{
    PipelineConfig,
    ProviderConfig,
    AgentProfile,
    QualityGate,
    ReviewerConfig
}

/// <summary>
/// Input data for the migration atomicity property test.
/// Contains valid field values plus which slot receives corrupt JSON.
/// </summary>
public record MigrationAtomicityInput(
    ConfigSlot CorruptSlot,
    string InvalidJson,
    int MaxRetries,
    string ProviderDisplayName,
    string ProfileDisplayName,
    string QualityGateDisplayName,
    string ReviewerDisplayName);

/// <summary>
/// FsCheck arbitrary generators for Migration Atomicity property tests.
/// Generates valid config field values and selects a random slot for corruption.
/// </summary>
public class MigrationAtomicityArbitraries
{
    private static readonly string[] InvalidJsonSamples =
    [
        "{broken",                    // Unterminated object
        "{ \"key\": }",              // Missing value
        "not json at all",           // Plain text
        "{ unclosed: true",          // Unquoted key + unterminated
        "[1, 2, 3",                  // Unterminated array
        "{'single': 'quotes'}",      // Single quotes invalid in JSON
        "",                          // Empty string (not valid JSON)
        "null",                      // Valid JSON but deserializes to null → caught
        "{{{{",                      // Repeated braces
        "{ \"a\": undefined }",      // undefined not valid in JSON
        "\x00\x01\x02"              // Binary garbage
    ];

    private static readonly string[] DisplayNames =
    [
        "Test Provider",
        "GitHub Issue Provider",
        "Custom Agent",
        "Production Profile",
        "Security Gate"
    ];

    public static Arbitrary<MigrationAtomicityInput> MigrationAtomicityInputArb()
    {
        var gen =
            from slot in Gen.Elements(
                ConfigSlot.PipelineConfig,
                ConfigSlot.ProviderConfig,
                ConfigSlot.AgentProfile,
                ConfigSlot.QualityGate,
                ConfigSlot.ReviewerConfig)
            from invalidJsonIdx in Gen.Choose(0, InvalidJsonSamples.Length - 1)
            from maxRetries in Gen.Choose(1, 10)
            from providerNameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from profileNameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from qgNameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from reviewerNameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            select new MigrationAtomicityInput(
                slot,
                InvalidJsonSamples[invalidJsonIdx],
                maxRetries,
                DisplayNames[providerNameIdx],
                DisplayNames[profileNameIdx],
                DisplayNames[qgNameIdx],
                DisplayNames[reviewerNameIdx]);
        return gen.ToArbitrary();
    }
}
