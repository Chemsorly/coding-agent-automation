// Feature: 029-pipeline-projects
// Property 1: Migration Idempotency
// Verify Migrate(Migrate(state)) == Migrate(state) — running N times produces same result as once.
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Infrastructure.Persistence;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for Migration Idempotency.
/// Running MigrateToProjectsAsync multiple times produces the same result as running it once.
/// **Validates: Requirements 2.5, 11.1, 11.5**
/// </summary>
public class MigrationIdempotencyPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public MigrationIdempotencyPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-idempotency-pbt-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 1: Migration Idempotency — Migrate(Migrate(state)) == Migrate(state).
    /// For any initial set of templates in PipelineConfiguration (0..N templates),
    /// running MigrateToProjectsAsync twice produces the same project store state
    /// as running it once.
    /// **Validates: Requirements 2.5, 11.1, 11.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(MigrationStateArbitraries) })]
    public void MigrateToProjectsAsync_IsIdempotent_RunningTwiceEqualsOnce(
        MigrationInitialState initialState)
    {
        // Setup: two identical stores with same initial config
        var dir1 = Path.Combine(_tempDir, $"run1-{Guid.NewGuid()}");
        var dir2 = Path.Combine(_tempDir, $"run2-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var store1 = new JsonConfigurationStore(dir1);
        var store2 = new JsonConfigurationStore(dir2);

        // Seed both stores with the same pipeline config (containing templates)
        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = initialState.Templates
        };

        store1.SavePipelineConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();
        store2.SavePipelineConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();

        // Store 1: Run migration ONCE
        ProjectMigrationService.MigrateToProjectsAsync(store1, store1, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Store 2: Run migration TWICE
        ProjectMigrationService.MigrateToProjectsAsync(store2, store2, CancellationToken.None)
            .GetAwaiter().GetResult();
        ProjectMigrationService.MigrateToProjectsAsync(store2, store2, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert: project store state after one run == state after two runs
        var projectsAfterOnce = store1.LoadProjectsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var projectsAfterTwice = store2.LoadProjectsAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(projectsAfterOnce.Count, projectsAfterTwice.Count);

        // Sort both by Id for deterministic comparison
        var sorted1 = projectsAfterOnce.OrderBy(p => p.Id).ToList();
        var sorted2 = projectsAfterTwice.OrderBy(p => p.Id).ToList();

        for (int i = 0; i < sorted1.Count; i++)
        {
            Assert.Equal(sorted1[i].Id, sorted2[i].Id);
            Assert.Equal(sorted1[i].Name, sorted2[i].Name);
            Assert.Equal(sorted1[i].Enabled, sorted2[i].Enabled);
            Assert.Equal(sorted1[i].TemplateIds, sorted2[i].TemplateIds);
            Assert.Equal(sorted1[i].EpicIssueProviderId, sorted2[i].EpicIssueProviderId);
        }
    }

    /// <summary>
    /// Property 1b: Migration idempotency when run N times (N >= 1).
    /// For any N >= 1, running MigrateToProjectsAsync N times produces the same
    /// result as running it once.
    /// **Validates: Requirements 2.5, 11.1, 11.5**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(MigrationStateArbitraries) })]
    public void MigrateToProjectsAsync_IsIdempotent_RunningNTimesEqualsOnce(
        MigrationInitialState initialState,
        PositiveInt runCount)
    {
        // Clamp run count to reasonable range (2..5) for test speed
        var n = Math.Clamp(runCount.Get, 2, 5);

        var dir1 = Path.Combine(_tempDir, $"once-{Guid.NewGuid()}");
        var dirN = Path.Combine(_tempDir, $"ntimes-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dirN);

        var storeOnce = new JsonConfigurationStore(dir1);
        var storeN = new JsonConfigurationStore(dirN);

        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = initialState.Templates
        };

        storeOnce.SavePipelineConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();
        storeN.SavePipelineConfigAsync(config, CancellationToken.None).GetAwaiter().GetResult();

        // Run once
        ProjectMigrationService.MigrateToProjectsAsync(storeOnce, storeOnce, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Run N times
        for (int i = 0; i < n; i++)
        {
            ProjectMigrationService.MigrateToProjectsAsync(storeN, storeN, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        // Compare
        var projectsOnce = storeOnce.LoadProjectsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var projectsN = storeN.LoadProjectsAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(projectsOnce.Count, projectsN.Count);

        var sorted1 = projectsOnce.OrderBy(p => p.Id).ToList();
        var sortedN = projectsN.OrderBy(p => p.Id).ToList();

        for (int i = 0; i < sorted1.Count; i++)
        {
            Assert.Equal(sorted1[i].Id, sortedN[i].Id);
            Assert.Equal(sorted1[i].Name, sortedN[i].Name);
            Assert.Equal(sorted1[i].Enabled, sortedN[i].Enabled);
            Assert.Equal(sorted1[i].TemplateIds, sortedN[i].TemplateIds);
        }
    }
}

/// <summary>
/// Initial state for migration property tests — represents the PipelineConfiguration
/// state before any migration has run (with 0..N templates).
/// </summary>
public sealed record MigrationInitialState
{
    public required IReadOnlyList<PipelineJobTemplate> Templates { get; init; }
}

/// <summary>
/// FsCheck arbitrary generators for migration idempotency tests.
/// Generates random PipelineConfiguration states with varying template counts.
/// </summary>
public class MigrationStateArbitraries
{
    private static readonly string[] TemplateNamePool =
        ["DotNet-Main", "Java-Backend", "Python-ML", "React-Frontend", "Rust-Core", "Go-Services"];

    public static Arbitrary<MigrationInitialState> MigrationInitialStateArb()
    {
        var templateGen =
            from id in GenGuid()
            from nameIdx in Gen.Choose(0, TemplateNamePool.Length - 1)
            from issueProviderId in GenGuid()
            from repoProviderId in GenGuid()
            from enabled in Gen.Elements(true, false)
            from decompositionEnabled in Gen.Elements(true, false)
            select new PipelineJobTemplate
            {
                Id = id,
                Name = TemplateNamePool[nameIdx],
                IssueProviderId = issueProviderId,
                RepoProviderId = repoProviderId,
                Enabled = enabled,
                DecompositionEnabled = decompositionEnabled
            };

        var stateGen =
            from count in Gen.Choose(0, 8)
            from templates in Gen.ArrayOf(templateGen).Resize(count)
            select new MigrationInitialState
            {
                Templates = templates.ToList().AsReadOnly()
            };

        return stateGen.ToArbitrary();
    }

    private static Gen<string> GenGuid()
        => Gen.Constant(0).Select(_ => Guid.NewGuid().ToString());
}
