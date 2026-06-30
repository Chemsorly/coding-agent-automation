using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for FileSystemHarnessSuggestionStore — filesystem-backed IHarnessSuggestionStore.
/// </summary>
public sealed class FileSystemHarnessSuggestionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FileSystemHarnessSuggestionStore _sut;

    public FileSystemHarnessSuggestionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"harness-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "harness-suggestions.json");
        _sut = new FileSystemHarnessSuggestionStore(_filePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task GetAsync_NoFile_ReturnsNull()
    {
        var result = await _sut.GetAsync(CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenGet_RoundTrips()
    {
        var suggestions = CreateSuggestions(5, 0.85m);

        await _sut.SaveAsync(suggestions, CancellationToken.None);
        var loaded = await _sut.GetAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(5);
        loaded.SuccessRate.Should().Be(0.85m);
        loaded.Suggestions.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_Overwrites_Previous()
    {
        await _sut.SaveAsync(CreateSuggestions(3, 0.5m), CancellationToken.None);
        await _sut.SaveAsync(CreateSuggestions(10, 0.9m), CancellationToken.None);

        var loaded = await _sut.GetAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(10);
        loaded.SuccessRate.Should().Be(0.9m);
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNeeded()
    {
        var nested = Path.Combine(_tempDir, "sub", "dir", "suggestions.json");
        var store = new FileSystemHarnessSuggestionStore(nested);

        await store.SaveAsync(CreateSuggestions(1, 1.0m), CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
    }

    private static HarnessSuggestions CreateSuggestions(int runCount, decimal rate) => new()
    {
        BasedOnRunCount = runCount,
        GeneratedAtUtc = DateTime.UtcNow,
        SuccessRate = rate,
        Suggestions = new List<HarnessSuggestion>
        {
            new() { Frequency = 3, Rationale = "Test rationale", Text = "Test suggestion text" }
        }
    };
}
