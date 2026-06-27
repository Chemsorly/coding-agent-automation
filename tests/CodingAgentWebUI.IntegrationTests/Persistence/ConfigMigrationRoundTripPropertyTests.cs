// Feature: 035a-postgres-work-queue
// Property 5: Config Migration Round-Trip
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
/// Property 5: Config Migration Round-Trip
/// Generate valid JSON configs, import to DB via ConfigMigrationService,
/// read back from DB, assert equivalence via JSON deserialization.
/// **Validates: Requirements 2.10**
/// </summary>
public class ConfigMigrationRoundTripPropertyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .Build();

    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Create schema once — iterations will truncate tables
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
    /// Truncates all tables to provide a fresh DB state for each iteration.
    /// </summary>
    private async Task TruncateAllTablesAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE "PipelineConfig", "ProviderConfigs", "AgentProfiles",
                           "QualityGateConfigs", "ReviewerConfigs", "Projects",
                           "PipelineJobTemplates", "ConsolidationRuns", "PipelineRuns",
                           "WorkItems" CASCADE
            """);
    }

    /// <summary>
    /// Property 5: Config Migration Round-Trip — PipelineConfiguration
    /// For any valid PipelineConfiguration, writing it to a JSON file, running migration,
    /// then reading from DB SHALL produce an equivalent configuration.
    /// **Validates: Requirements 2.10**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ConfigMigrationRoundTripArbitraries) })]
    public async Task PipelineConfig_MigrateToDb_RoundTripsEquivalent(
        MigrationRoundTripTestData testData)
    {
        // Fresh state for each iteration
        await TruncateAllTablesAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), $"config-roundtrip-{Guid.NewGuid():N}");
        try
        {
            // 1. Write generated config to temp directory
            Directory.CreateDirectory(tempDir);
            var configJson = JsonSerializer.Serialize(testData.Config, PipelineJsonOptions.Default);
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "pipeline-config.json"), configJson);

            // 2. Run migration to import into DB
            var factory = CreateFactory();
            var lockProvider = new TestLockProvider();
            var migrationService = new ConfigMigrationService(factory, lockProvider, tempDir);

            var migrated = await migrationService.MigrateIfNeededAsync(CancellationToken.None);
            Assert.True(migrated, "Migration should have run on empty DB");

            // 3. Read back from DB
            await using var readCtx = CreateContext();
            var entity = await readCtx.PipelineConfig.AsNoTracking().FirstOrDefaultAsync();
            Assert.NotNull(entity);
            Assert.NotNull(entity.Configuration);

            var exported = JsonSerializer.Deserialize<PipelineConfiguration>(
                entity.Configuration.RootElement.GetRawText(), PipelineJsonOptions.Default);
            Assert.NotNull(exported);

            // 4. Compare via JSON equivalence (not string equality — formatting may differ)
            var originalJson = JsonSerializer.Serialize(testData.Config, PipelineJsonOptions.Default);
            var exportedJson = JsonSerializer.Serialize(exported, PipelineJsonOptions.Default);
            Assert.Equal(originalJson, exportedJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 5: Config Migration Round-Trip — ProviderConfigs
    /// For any valid ProviderConfig, writing to file, running migration,
    /// then reading from DB SHALL produce an equivalent config.
    /// **Validates: Requirements 2.10**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ConfigMigrationRoundTripArbitraries) })]
    public async Task ProviderConfig_MigrateToDb_RoundTripsEquivalent(
        ProviderRoundTripTestData testData)
    {
        await TruncateAllTablesAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), $"config-roundtrip-{Guid.NewGuid():N}");
        try
        {
            // 1. Write pipeline-config.json (required for migration to run)
            Directory.CreateDirectory(tempDir);
            var pipelineConfig = new PipelineConfiguration();
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "pipeline-config.json"),
                JsonSerializer.Serialize(pipelineConfig, PipelineJsonOptions.Default));

            // Write provider config to appropriate subdirectory
            var kindDir = testData.Config.Kind switch
            {
                ProviderKind.Issue => "issue",
                ProviderKind.Repository => "repository",
                ProviderKind.Agent => "agent",
                ProviderKind.Brain => "brain",
                ProviderKind.Pipeline => "pipeline",
                _ => "issue"
            };
            var providerDir = Path.Combine(tempDir, "providers", kindDir);
            Directory.CreateDirectory(providerDir);
            var providerJson = JsonSerializer.Serialize(testData.Config, PipelineJsonOptions.Default);
            await File.WriteAllTextAsync(
                Path.Combine(providerDir, $"{testData.Config.Id}.json"), providerJson);

            // 2. Run migration
            var factory = CreateFactory();
            var lockProvider = new TestLockProvider();
            var migrationService = new ConfigMigrationService(factory, lockProvider, tempDir);
            await migrationService.MigrateIfNeededAsync(CancellationToken.None);

            // 3. Read back from DB
            await using var readCtx = CreateContext();
            var entity = await readCtx.ProviderConfigs.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == Guid.Parse(testData.Config.Id));
            Assert.NotNull(entity);
            Assert.NotNull(entity.Configuration);

            var exported = JsonSerializer.Deserialize<ProviderConfig>(
                entity.Configuration.RootElement.GetRawText(), PipelineJsonOptions.Default);
            Assert.NotNull(exported);

            // 4. Compare via semantic equivalence (not string equality — dictionary order may differ)
            Assert.Equal(testData.Config.Id, exported.Id);
            Assert.Equal(testData.Config.Kind, exported.Kind);
            Assert.Equal(testData.Config.ProviderType, exported.ProviderType);
            Assert.Equal(testData.Config.DisplayName, exported.DisplayName);
            Assert.Equal(testData.Config.Settings.Count, exported.Settings.Count);
            foreach (var kv in testData.Config.Settings)
            {
                Assert.True(exported.Settings.ContainsKey(kv.Key),
                    $"Exported settings missing key '{kv.Key}'");
                Assert.Equal(kv.Value, exported.Settings[kv.Key]);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Property 5: Config Migration Round-Trip — AgentProfiles
    /// **Validates: Requirements 2.10**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ConfigMigrationRoundTripArbitraries) })]
    public async Task AgentProfile_MigrateToDb_RoundTripsEquivalent(
        AgentProfileRoundTripTestData testData)
    {
        await TruncateAllTablesAsync();

        var tempDir = Path.Combine(Path.GetTempPath(), $"config-roundtrip-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "pipeline-config.json"),
                JsonSerializer.Serialize(new PipelineConfiguration(), PipelineJsonOptions.Default));

            // Write profile
            var profilesDir = Path.Combine(tempDir, "profiles");
            Directory.CreateDirectory(profilesDir);
            var profileJson = JsonSerializer.Serialize(testData.Profile, PipelineJsonOptions.Default);
            await File.WriteAllTextAsync(
                Path.Combine(profilesDir, $"{testData.Profile.Id}.json"), profileJson);

            // Run migration
            var factory = CreateFactory();
            var lockProvider = new TestLockProvider();
            var migrationService = new ConfigMigrationService(factory, lockProvider, tempDir);
            await migrationService.MigrateIfNeededAsync(CancellationToken.None);

            // Read back
            await using var readCtx = CreateContext();
            var entity = await readCtx.AgentProfiles.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == Guid.Parse(testData.Profile.Id));
            Assert.NotNull(entity);
            Assert.NotNull(entity.Configuration);

            var exported = JsonSerializer.Deserialize<AgentProfile>(
                entity.Configuration.RootElement.GetRawText(), PipelineJsonOptions.Default);
            Assert.NotNull(exported);

            var originalJson = JsonSerializer.Serialize(testData.Profile, PipelineJsonOptions.Default);
            var exportedJson = JsonSerializer.Serialize(exported, PipelineJsonOptions.Default);
            Assert.Equal(originalJson, exportedJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Test helpers ─────────────────────────────────────────────────────

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Simple no-op lock provider for tests (single-threaded, no contention).
    /// </summary>
    private sealed class TestLockProvider : IDistributedLockProvider
    {
        public Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

// ── FsCheck wrapper types ────────────────────────────────────────────────

public record MigrationRoundTripTestData(PipelineConfiguration Config);
public record ProviderRoundTripTestData(ProviderConfig Config);
public record AgentProfileRoundTripTestData(AgentProfile Profile);

// ── FsCheck generators ───────────────────────────────────────────────────

/// <summary>
/// FsCheck arbitrary generators for Config Migration Round-Trip property tests.
/// Generates valid PipelineConfiguration, ProviderConfig, and AgentProfile instances.
/// </summary>
public class ConfigMigrationRoundTripArbitraries
{
    private static readonly string[] PromptPool =
    [
        "Analyze the code carefully",
        "Fix bugs and improve quality",
        "Review for security issues",
        "Implement the feature as specified",
        "Run all tests and verify"
    ];

    private static readonly string[] PathPool =
    [
        ".env", "secrets/", "node_modules/", ".git/", "bin/", "obj/"
    ];

    private static readonly string[] ProviderTypes =
    [
        "GitHub", "GitLab", "AzureDevOps", "Bitbucket"
    ];

    private static readonly string[] ProfileNames =
    [
        "Default", "Fast", "Thorough", "Minimal", "Custom"
    ];

    public static Arbitrary<MigrationRoundTripTestData> MigrationRoundTripTestDataArb()
    {
        var gen =
            from config in GenPipelineConfiguration()
            select new MigrationRoundTripTestData(config);
        return gen.ToArbitrary();
    }

    public static Arbitrary<ProviderRoundTripTestData> ProviderRoundTripTestDataArb()
    {
        var gen =
            from config in GenProviderConfig()
            select new ProviderRoundTripTestData(config);
        return gen.ToArbitrary();
    }

    public static Arbitrary<AgentProfileRoundTripTestData> AgentProfileRoundTripTestDataArb()
    {
        var gen =
            from profile in GenAgentProfile()
            select new AgentProfileRoundTripTestData(profile);
        return gen.ToArbitrary();
    }

    private static Gen<PipelineConfiguration> GenPipelineConfiguration()
    {
        return
            from maxRetries in Gen.Choose(0, 10)
            from maxAnalysisRetries in Gen.Choose(0, 5)
            from issuePageSize in Gen.Choose(1, 100)
            from boolBits in Gen.Choose(0, 127)
            from analysisPromptIdx in Gen.Choose(0, PromptPool.Length - 1)
            from implementationPromptIdx in Gen.Choose(0, PromptPool.Length - 1)
            from pathMask in Gen.Choose(0, 63)
            from maxDecompSubIssues in Gen.Choose(1, 20)
            from maxConcurrentDecomp in Gen.Choose(1, 5)
            from maxRefactoringProposals in Gen.Choose(1, 10)
            from maxOpenIssuesForContext in Gen.Choose(1, 100)
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
                BrainReadOnly = (boolBits & 64) != 0,
                AnalysisPrompt = PromptPool[analysisPromptIdx],
                ImplementationPrompt = PromptPool[implementationPromptIdx],
                BlacklistedPaths = PathPool
                    .Where((_, i) => (pathMask & (1 << i)) != 0)
                    .ToList(),
                MaxDecompositionSubIssues = maxDecompSubIssues,
                MaxConcurrentDecompositions = maxConcurrentDecomp,
                MaxRefactoringProposals = maxRefactoringProposals,
                MaxOpenIssuesForContext = maxOpenIssuesForContext
            };
    }

    private static Gen<ProviderConfig> GenProviderConfig()
    {
        return
            from kind in Gen.Elements(
                ProviderKind.Issue, ProviderKind.Repository,
                ProviderKind.Agent, ProviderKind.Brain, ProviderKind.Pipeline)
            from providerTypeIdx in Gen.Choose(0, ProviderTypes.Length - 1)
            from displayNameIdx in Gen.Choose(0, ProfileNames.Length - 1)
            from settingsCount in Gen.Choose(0, 3)
            from settingsKeys in Gen.ArrayOf(
                Gen.Elements("url", "token", "org", "project", "branch"), settingsCount)
            from settingsValues in Gen.ArrayOf(
                Gen.Elements("https://github.com", "main", "my-org", "my-project"), settingsCount)
            select new ProviderConfig
            {
                Id = Guid.NewGuid().ToString(),
                Kind = kind,
                ProviderType = ProviderTypes[providerTypeIdx],
                DisplayName = $"{ProfileNames[displayNameIdx]}-{kind}",
                Settings = settingsKeys.Zip(settingsValues)
                    .DistinctBy(kv => kv.First)
                    .ToDictionary(kv => kv.First, kv => kv.Second)
            };
    }

    private static Gen<AgentProfile> GenAgentProfile()
    {
        return
            from nameIdx in Gen.Choose(0, ProfileNames.Length - 1)
            from labelCount in Gen.Choose(0, 3)
            from labels in Gen.ArrayOf(
                Gen.Elements("kiro", "dotnet", "java", "python", "opencode"), labelCount)
            from priority in Gen.Choose(0, 10)
            from enabled in Gen.Elements(true, false)
            select new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"Profile-{ProfileNames[nameIdx]}",
                AgentProviderConfigId = Guid.NewGuid().ToString(),
                MatchLabels = labels.Distinct().ToList(),
                Priority = priority,
                Enabled = enabled
            };
    }
}
