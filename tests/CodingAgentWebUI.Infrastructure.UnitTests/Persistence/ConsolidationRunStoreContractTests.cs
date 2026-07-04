using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Contract tests for <see cref="IConsolidationRunStore"/> implementations.
/// Both FileSystem-backed and Postgres-backed stores must satisfy these behavioral contracts.
/// Prevents behavioral drift between legacy (filesystem) and DB (Postgres) modes.
///
/// Pattern follows <see cref="ConfigurationStoreContractTests"/>.
/// Derived classes provide a concrete store instance via <see cref="CreateStore"/>.
/// </summary>
public abstract class ConsolidationRunStoreContractTests : IDisposable
{
    /// <summary>Create a fresh store instance for isolation between tests.</summary>
    protected abstract IConsolidationRunStore CreateStore();

    /// <summary>Cleanup resources after each test.</summary>
    public virtual void Dispose() { }

    // ── SaveRunAsync + GetByIdAsync ─────────────────────────────────────

    [Fact]
    public async Task SaveRun_ThenGetById_ReturnsSavedRun()
    {
        var store = CreateStore();
        var run = CreateRun();

        await store.SaveRunAsync(run, CancellationToken.None);
        var loaded = await store.GetByIdAsync(run.RunId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be(run.RunId);
        loaded.Type.Should().Be(run.Type);
        loaded.Status.Should().Be(run.Status);
        loaded.TemplateId.Should().Be(run.TemplateId);
        loaded.TemplateName.Should().Be(run.TemplateName);
    }

    [Fact]
    public async Task GetById_NonExistent_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.GetByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetById_InvalidGuid_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.GetByIdAsync("not-a-guid", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── SaveRunAsync updates (upsert behavior) ──────────────────────────

    [Fact]
    public async Task SaveRun_Twice_UpdatesExisting()
    {
        var store = CreateStore();
        var run = CreateRun(status: ConsolidationRunStatus.Running);

        await store.SaveRunAsync(run, CancellationToken.None);

        // Simulate completion
        run.Status = ConsolidationRunStatus.Succeeded;
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.Summary = "Completed successfully";
        run.TotalTokens = 12345;

        await store.SaveRunAsync(run, CancellationToken.None);

        var loaded = await store.GetByIdAsync(run.RunId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ConsolidationRunStatus.Succeeded);
        loaded.CompletedAtUtc.Should().NotBeNull();
        loaded.Summary.Should().Be("Completed successfully");
        loaded.TotalTokens.Should().Be(12345);
    }

    // ── LoadAllRunsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllRuns_EmptyStore_ReturnsEmptyList()
    {
        var store = CreateStore();

        var runs = await store.LoadAllRunsAsync(CancellationToken.None);

        runs.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllRuns_MultipleRuns_ReturnsAll()
    {
        var store = CreateStore();
        var run1 = CreateRun(type: ConsolidationRunType.BrainConsolidation);
        var run2 = CreateRun(type: ConsolidationRunType.RefactoringDetection);
        var run3 = CreateRun(type: ConsolidationRunType.HarnessSuggestions);

        await store.SaveRunAsync(run1, CancellationToken.None);
        await store.SaveRunAsync(run2, CancellationToken.None);
        await store.SaveRunAsync(run3, CancellationToken.None);

        var runs = await store.LoadAllRunsAsync(CancellationToken.None);

        runs.Should().HaveCount(3);
        runs.Should().Contain(r => r.RunId == run1.RunId);
        runs.Should().Contain(r => r.RunId == run2.RunId);
        runs.Should().Contain(r => r.RunId == run3.RunId);
    }

    // ── DeleteRunAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRun_ExistingRun_RemovesFromStore()
    {
        var store = CreateStore();
        var run = CreateRun();

        await store.SaveRunAsync(run, CancellationToken.None);
        await store.DeleteRunAsync(run.RunId, CancellationToken.None);

        var loaded = await store.GetByIdAsync(run.RunId, CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRun_NonExistent_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.DeleteRunAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteRun_InvalidGuid_DoesNotThrow()
    {
        var store = CreateStore();

        var act = () => store.DeleteRunAsync("not-a-guid", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteRun_DoesNotAffectOtherRuns()
    {
        var store = CreateStore();
        var run1 = CreateRun();
        var run2 = CreateRun();

        await store.SaveRunAsync(run1, CancellationToken.None);
        await store.SaveRunAsync(run2, CancellationToken.None);

        await store.DeleteRunAsync(run1.RunId, CancellationToken.None);

        var remaining = await store.LoadAllRunsAsync(CancellationToken.None);
        remaining.Should().HaveCount(1);
        remaining.Should().Contain(r => r.RunId == run2.RunId);
    }

    // ── Data fidelity ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveRun_PreservesAllProperties()
    {
        var store = CreateStore();
        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-42",
            TemplateName = "Main Project",
            StartedAtUtc = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero),
            CompletedAtUtc = new DateTimeOffset(2026, 6, 15, 10, 35, 0, TimeSpan.Zero),
            Status = ConsolidationRunStatus.Succeeded,
            Summary = "Detected 3 refactoring opportunities",
            TotalTokens = 54321,
            QueuedRequiredLabels = new[] { "kiro", "dotnet" }
        };

        await store.SaveRunAsync(run, CancellationToken.None);
        var loaded = await store.GetByIdAsync(run.RunId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be(run.RunId);
        loaded.Type.Should().Be(ConsolidationRunType.RefactoringDetection);
        loaded.TemplateId.Should().Be("template-42");
        loaded.TemplateName.Should().Be("Main Project");
        loaded.StartedAtUtc.Should().Be(run.StartedAtUtc);
        loaded.CompletedAtUtc.Should().Be(run.CompletedAtUtc);
        loaded.Status.Should().Be(ConsolidationRunStatus.Succeeded);
        loaded.Summary.Should().Be("Detected 3 refactoring opportunities");
        loaded.TotalTokens.Should().Be(54321);
        loaded.QueuedRequiredLabels.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ConsolidationRun CreateRun(
        ConsolidationRunType type = ConsolidationRunType.BrainConsolidation,
        ConsolidationRunStatus status = ConsolidationRunStatus.Running)
    {
        return new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = type,
            TemplateId = "test-template",
            TemplateName = "Test Template",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = status
        };
    }
}

// ── FileSystem-backed implementation ────────────────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="FileSystemConsolidationRunStore"/>.
/// </summary>
public class FileSystemConsolidationRunStoreContractTests : ConsolidationRunStoreContractTests
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"contract-consol-fs-{Guid.NewGuid()}");

    public FileSystemConsolidationRunStoreContractTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    protected override IConsolidationRunStore CreateStore() => new FileSystemConsolidationRunStore(_tempDir);

    public override void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

// ── Postgres-backed implementation (InMemory EF) ────────────────────────────

/// <summary>
/// Runs the contract tests against <see cref="PostgresConsolidationRunStore"/> using InMemory EF Core.
/// </summary>
public class PostgresConsolidationRunStoreContractTests : ConsolidationRunStoreContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresConsolidationRunStoreContractTests()
    {
        var dbName = $"ConsolContractTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new PipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    protected override IConsolidationRunStore CreateStore()
    {
        var factory = new ConsolContractTestDbContextFactory(_dbOptions);
        return new PostgresConsolidationRunStore(factory);
    }

    public override void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }
}

/// <summary>Helper: IDbContextFactory for InMemory provider.</summary>
file class ConsolContractTestDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _options;
    public ConsolContractTestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
    public PipelineDbContext CreateDbContext() => new(_options);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}
