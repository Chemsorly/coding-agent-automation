using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Shared contract tests for <see cref="IHarnessSuggestionStore"/>.
/// Both FileSystem and Postgres implementations must exhibit identical behavioral semantics.
/// </summary>
public abstract class HarnessSuggestionStoreContractTests
{
    protected abstract IHarnessSuggestionStore CreateStore();

    private static HarnessSuggestions CreateSampleSuggestions(int count = 2) => new()
    {
        GeneratedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
        BasedOnRunCount = 25,
        SuccessRate = 0.88m,
        Suggestions = Enumerable.Range(0, count).Select(i => new HarnessSuggestion
        {
            Text = $"Suggestion {i}: improve agent timeout handling",
            Rationale = $"Occurred in {i + 3} of 25 runs",
            Frequency = i + 3
        }).ToList()
    };

    [Fact]
    public async Task GetAsync_WhenNothingSaved_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetAsync(CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var store = CreateStore();
        var original = CreateSampleSuggestions();

        await store.SaveAsync(original, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.GeneratedAtUtc.Should().Be(original.GeneratedAtUtc);
        loaded.BasedOnRunCount.Should().Be(original.BasedOnRunCount);
        loaded.SuccessRate.Should().Be(original.SuccessRate);
        loaded.Suggestions.Should().HaveCount(2);
        loaded.Suggestions[0].Text.Should().Be(original.Suggestions[0].Text);
        loaded.Suggestions[0].Rationale.Should().Be(original.Suggestions[0].Rationale);
        loaded.Suggestions[0].Frequency.Should().Be(original.Suggestions[0].Frequency);
        loaded.Suggestions[1].Text.Should().Be(original.Suggestions[1].Text);
    }

    [Fact]
    public async Task Save_OverwritesPrevious()
    {
        var store = CreateStore();

        var first = CreateSampleSuggestions(1);
        await store.SaveAsync(first, CancellationToken.None);

        var second = new HarnessSuggestions
        {
            GeneratedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            BasedOnRunCount = 50,
            SuccessRate = 0.92m,
            Suggestions = Enumerable.Range(0, 3).Select(i => new HarnessSuggestion
            {
                Text = $"Suggestion {i}: improve agent timeout handling",
                Rationale = $"Occurred in {i + 3} of 25 runs",
                Frequency = i + 3
            }).ToList()
        };
        await store.SaveAsync(second, CancellationToken.None);

        var loaded = await store.GetAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(50);
        loaded.SuccessRate.Should().Be(0.92m);
        loaded.Suggestions.Should().HaveCount(3);
    }

    [Fact]
    public async Task Save_NullSuggestions_ThrowsArgumentNullException()
    {
        var store = CreateStore();

        var act = () => store.SaveAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

/// <summary>
/// Runs <see cref="IHarnessSuggestionStore"/> contract tests against <see cref="FileSystemHarnessSuggestionStore"/>.
/// </summary>
public sealed class FileSystemHarnessSuggestionStoreContractTests
    : HarnessSuggestionStoreContractTests, IDisposable
{
    private readonly string _tempDir;

    public FileSystemHarnessSuggestionStoreContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"harness-store-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    protected override IHarnessSuggestionStore CreateStore()
        => new FileSystemHarnessSuggestionStore(Path.Combine(_tempDir, "suggestions.json"));
}

/// <summary>
/// Runs <see cref="IHarnessSuggestionStore"/> contract tests against <see cref="PostgresHarnessSuggestionStore"/>.
/// Uses EF Core InMemory provider — no real Postgres required.
/// </summary>
public sealed class PostgresHarnessSuggestionStoreContractTests : HarnessSuggestionStoreContractTests
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;

    public PostgresHarnessSuggestionStoreContractTests()
    {
        var dbName = $"HarnessStoreContract-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
    }

    protected override IHarnessSuggestionStore CreateStore()
        => new PostgresHarnessSuggestionStore(new InMemoryDbContextFactory(_dbOptions));

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
    }
}
