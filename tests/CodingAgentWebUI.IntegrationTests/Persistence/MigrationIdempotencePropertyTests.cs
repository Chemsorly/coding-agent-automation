// Feature: 035a-postgres-work-queue
// Property 6: Migration Idempotence
using System.Collections.Concurrent;
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
/// Property 6: Migration Idempotence
/// Generate random config files, run migration N times, assert DB state identical after 1st and Nth run.
/// The migration service skips if PipelineConfig row exists, so running it multiple times
/// must never duplicate or mutate data.
/// **Validates: Requirements 2.3**
/// </summary>
public class MigrationIdempotencePropertyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .Build();

    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Apply schema once — iterations use TRUNCATE to reset state
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

    /// <summary>
    /// Property 6: Migration Idempotence — running migration N times produces identical DB state.
    /// For any valid config file set, running MigrateIfNeededAsync twice (or more) produces
    /// the same entity counts and config values as after the first run.
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MigrationIdempotenceArbitraries) })]
    public async Task MigrationRunMultipleTimes_ProducesIdenticalState(MigrationConfigSet configSet)
    {
        // Clean all tables between iterations to get fresh state
        await TruncateAllTablesAsync();

        // Write config files to temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"migration-idem-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            WriteConfigFiles(tempDir, configSet);

            var lockProvider = new TestDistributedLockProvider();
            var factory = new TestDbContextFactory(_connectionString);

            // Run migration first time
            var service = new ConfigMigrationService(factory, lockProvider, tempDir);
            var firstResult = await service.MigrateIfNeededAsync(CancellationToken.None);
            Assert.True(firstResult, "First migration should perform the import");

            // Capture DB state after first migration
            var stateAfterFirst = await CaptureDbState();

            // Run migration N more times (N=2 to keep each iteration fast)
            for (int i = 0; i < 2; i++)
            {
                var subsequentResult = await service.MigrateIfNeededAsync(CancellationToken.None);
                Assert.False(subsequentResult, $"Migration run {i + 2} should be skipped (idempotent)");
            }

            // Capture DB state after Nth run
            var stateAfterNth = await CaptureDbState();

            // Assert: DB state after 1st run == DB state after Nth run
            Assert.Equal(stateAfterFirst.PipelineConfigCount, stateAfterNth.PipelineConfigCount);
            Assert.Equal(stateAfterFirst.ProviderConfigCount, stateAfterNth.ProviderConfigCount);
            Assert.Equal(stateAfterFirst.AgentProfileCount, stateAfterNth.AgentProfileCount);
            Assert.Equal(stateAfterFirst.QualityGateCount, stateAfterNth.QualityGateCount);
            Assert.Equal(stateAfterFirst.ReviewerCount, stateAfterNth.ReviewerCount);
            Assert.Equal(stateAfterFirst.ProjectCount, stateAfterNth.ProjectCount);
            Assert.Equal(stateAfterFirst.TemplateCount, stateAfterNth.TemplateCount);
            Assert.Equal(stateAfterFirst.ConsolidationRunCount, stateAfterNth.ConsolidationRunCount);
            Assert.Equal(stateAfterFirst.PipelineRunCount, stateAfterNth.PipelineRunCount);

            // Verify config JSON content is unchanged
            Assert.Equal(stateAfterFirst.PipelineConfigJson, stateAfterNth.PipelineConfigJson);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

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

    private async Task<DbState> CaptureDbState()
    {
        await using var ctx = CreateContext();

        var pipelineConfigs = await ctx.PipelineConfig.AsNoTracking().ToListAsync();
        var pipelineConfigJson = pipelineConfigs.Count > 0
            ? pipelineConfigs[0].Configuration?.RootElement.GetRawText() ?? ""
            : "";

        return new DbState
        {
            PipelineConfigCount = pipelineConfigs.Count,
            ProviderConfigCount = await ctx.ProviderConfigs.CountAsync(),
            AgentProfileCount = await ctx.AgentProfiles.CountAsync(),
            QualityGateCount = await ctx.QualityGateConfigs.CountAsync(),
            ReviewerCount = await ctx.ReviewerConfigs.CountAsync(),
            ProjectCount = await ctx.Projects.CountAsync(),
            TemplateCount = await ctx.PipelineJobTemplates.CountAsync(),
            ConsolidationRunCount = await ctx.ConsolidationRuns.CountAsync(),
            PipelineRunCount = await ctx.PipelineRuns.CountAsync(),
            PipelineConfigJson = pipelineConfigJson
        };
    }

    private static void WriteConfigFiles(string baseDir, MigrationConfigSet configSet)
    {
        var jsonOptions = PipelineJsonOptions.Default;

        // Pipeline config (always present — triggers migration)
        var pipelineJson = JsonSerializer.Serialize(configSet.PipelineConfig, jsonOptions);
        File.WriteAllText(Path.Combine(baseDir, "pipeline-config.json"), pipelineJson);

        // Provider configs
        if (configSet.ProviderConfigs.Count > 0)
        {
            var providerDir = Path.Combine(baseDir, "providers", "issue");
            Directory.CreateDirectory(providerDir);
            foreach (var provider in configSet.ProviderConfigs)
            {
                var json = JsonSerializer.Serialize(provider, jsonOptions);
                File.WriteAllText(Path.Combine(providerDir, $"{provider.Id}.json"), json);
            }
        }

        // Agent profiles
        if (configSet.AgentProfiles.Count > 0)
        {
            var profileDir = Path.Combine(baseDir, "profiles");
            Directory.CreateDirectory(profileDir);
            foreach (var profile in configSet.AgentProfiles)
            {
                var json = JsonSerializer.Serialize(profile, jsonOptions);
                File.WriteAllText(Path.Combine(profileDir, $"{profile.Id}.json"), json);
            }
        }

        // Quality gate configs
        if (configSet.QualityGateConfigs.Count > 0)
        {
            var qgDir = Path.Combine(baseDir, "quality-gates");
            Directory.CreateDirectory(qgDir);
            foreach (var qg in configSet.QualityGateConfigs)
            {
                var json = JsonSerializer.Serialize(qg, jsonOptions);
                File.WriteAllText(Path.Combine(qgDir, $"{qg.Id}.json"), json);
            }
        }

        // Reviewer configs
        if (configSet.ReviewerConfigs.Count > 0)
        {
            var revDir = Path.Combine(baseDir, "reviewers");
            Directory.CreateDirectory(revDir);
            foreach (var rev in configSet.ReviewerConfigs)
            {
                var json = JsonSerializer.Serialize(rev, jsonOptions);
                File.WriteAllText(Path.Combine(revDir, $"{rev.Id}.json"), json);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _connectionString;

        public TestDbContextFactory(string connectionString)
            => _connectionString = connectionString;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new PipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed record DbState
    {
        public int PipelineConfigCount { get; init; }
        public int ProviderConfigCount { get; init; }
        public int AgentProfileCount { get; init; }
        public int QualityGateCount { get; init; }
        public int ReviewerCount { get; init; }
        public int ProjectCount { get; init; }
        public int TemplateCount { get; init; }
        public int ConsolidationRunCount { get; init; }
        public int PipelineRunCount { get; init; }
        public string PipelineConfigJson { get; init; } = "";
    }

    /// <summary>
    /// Simple in-process distributed lock provider for tests.
    /// </summary>
    private sealed class TestDistributedLockProvider : IDistributedLockProvider
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        {
            var semaphore = _locks.GetOrAdd(lockName, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(ct);
            return new SemaphoreHandle(semaphore);
        }

        private sealed class SemaphoreHandle(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            private bool _disposed;

            public ValueTask DisposeAsync()
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}

/// <summary>
/// Represents a randomly generated set of config files for migration testing.
/// </summary>
public record MigrationConfigSet(
    PipelineConfiguration PipelineConfig,
    IReadOnlyList<ProviderConfig> ProviderConfigs,
    IReadOnlyList<AgentProfile> AgentProfiles,
    IReadOnlyList<QualityGateConfiguration> QualityGateConfigs,
    IReadOnlyList<ReviewerConfiguration> ReviewerConfigs);

/// <summary>
/// FsCheck arbitrary generators for Migration Idempotence property tests.
/// Generates varied config file content to exercise migration paths.
/// </summary>
public class MigrationIdempotenceArbitraries
{
    private static readonly string[] DisplayNames =
        ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta"];

    private static readonly string[] ProviderTypes =
        ["GitHub", "GitLab", "AzureDevOps", "Bitbucket"];

    private static readonly string[] Commands =
        ["dotnet build", "npm test", "mvn verify", "cargo test"];

    private static readonly string[] Prompts =
        ["Analyze the code", "Fix the bug", "Review for security", "Run tests", "Check performance"];

    public static Arbitrary<MigrationConfigSet> MigrationConfigSetArb()
    {
        var gen =
            from pipelineConfig in GenPipelineConfiguration()
            from providerCount in Gen.Choose(0, 3)
            from providers in Gen.ArrayOf(GenProviderConfig(), providerCount)
            from profileCount in Gen.Choose(0, 2)
            from profiles in Gen.ArrayOf(GenAgentProfile(), profileCount)
            from qgCount in Gen.Choose(0, 2)
            from qualityGates in Gen.ArrayOf(GenQualityGateConfig(), qgCount)
            from revCount in Gen.Choose(0, 2)
            from reviewers in Gen.ArrayOf(GenReviewerConfig(), revCount)
            select new MigrationConfigSet(
                pipelineConfig,
                providers.ToList(),
                profiles.ToList(),
                qualityGates.ToList(),
                reviewers.ToList());

        return gen.ToArbitrary();
    }

    private static Gen<PipelineConfiguration> GenPipelineConfiguration()
    {
        return
            from maxRetries in Gen.Choose(0, 10)
            from maxAnalysisRetries in Gen.Choose(0, 5)
            from issuePageSize in Gen.Choose(1, 100)
            from boolBits in Gen.Choose(0, 63)
            from analysisPromptIdx in Gen.Choose(0, Prompts.Length - 1)
            from implementationPromptIdx in Gen.Choose(0, Prompts.Length - 1)
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
                AnalysisPrompt = Prompts[analysisPromptIdx],
                ImplementationPrompt = Prompts[implementationPromptIdx]
            };
    }

    private static Gen<ProviderConfig> GenProviderConfig()
    {
        return
            from nameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from typeIdx in Gen.Choose(0, ProviderTypes.Length - 1)
            select new ProviderConfig
            {
                Id = Guid.NewGuid().ToString(),
                Kind = ProviderKind.Issue,
                ProviderType = ProviderTypes[typeIdx],
                DisplayName = $"{DisplayNames[nameIdx]} Provider"
            };
    }

    private static Gen<AgentProfile> GenAgentProfile()
    {
        return
            from nameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from priority in Gen.Choose(0, 10)
            from enabled in Gen.Elements(true, false)
            select new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"{DisplayNames[nameIdx]} Profile",
                AgentProviderConfigId = Guid.NewGuid().ToString(),
                MatchLabels = ["dotnet", "linux"],
                Enabled = enabled,
                Priority = priority
            };
    }

    private static Gen<QualityGateConfiguration> GenQualityGateConfig()
    {
        return
            from nameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from cmdIdx in Gen.Choose(0, Commands.Length - 1)
            select new QualityGateConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"{DisplayNames[nameIdx]} Gate",
                CompilationCommand = Commands[cmdIdx].Split(' ')[0],
                CompilationArguments = [Commands[cmdIdx].Split(' ')[1]]
            };
    }

    private static Gen<ReviewerConfiguration> GenReviewerConfig()
    {
        return
            from nameIdx in Gen.Choose(0, DisplayNames.Length - 1)
            from promptIdx in Gen.Choose(0, Prompts.Length - 1)
            select new ReviewerConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"{DisplayNames[nameIdx]} Reviewer",
                Agents = [new ReviewAgent { Name = DisplayNames[nameIdx], Prompt = Prompts[promptIdx] }]
            };
    }
}
