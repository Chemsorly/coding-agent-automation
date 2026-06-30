using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for FileSystemLoopStateStore — filesystem-backed ILoopStateStore implementation.
/// </summary>
public sealed class FileSystemLoopStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _stateFilePath;
    private readonly FileSystemLoopStateStore _sut;

    public FileSystemLoopStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loop-state-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _stateFilePath = Path.Combine(_tempDir, "loop-state.json");
        _sut = new FileSystemLoopStateStore(_stateFilePath);
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
    public async Task ReadAsync_NoFile_ReturnsNull()
    {
        var result = await _sut.ReadAsync(CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_ThenRead_RoundTrips()
    {
        var state = new LoopState
        {
            IsActive = true,
            StartedAt = DateTimeOffset.UtcNow
        };

        await _sut.WriteAsync(state, CancellationToken.None);
        var loaded = await _sut.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeTrue();
        loaded.StartedAt.Should().BeCloseTo(state.StartedAt.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WriteAsync_Overwrites_PreviousState()
    {
        await _sut.WriteAsync(new LoopState { IsActive = true, StartedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        await _sut.WriteAsync(new LoopState { IsActive = false, StoppedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

        var loaded = await _sut.ReadAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        loaded.StoppedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        await _sut.WriteAsync(new LoopState { IsActive = true }, CancellationToken.None);

        await _sut.DeleteAsync(CancellationToken.None);

        var result = await _sut.ReadAsync(CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NoFile_DoesNotThrow()
    {
        var act = () => _sut.DeleteAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectoryIfNeeded()
    {
        var nested = Path.Combine(_tempDir, "sub", "dir", "state.json");
        var store = new FileSystemLoopStateStore(nested);

        await store.WriteAsync(new LoopState { IsActive = true }, CancellationToken.None);

        var loaded = await store.ReadAsync(CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeTrue();
    }
}
