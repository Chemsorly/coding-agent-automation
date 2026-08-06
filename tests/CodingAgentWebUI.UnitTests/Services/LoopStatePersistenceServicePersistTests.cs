using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="LoopStatePersistenceService.PersistCurrentStateAsync"/> — the path
/// that contains <c>_writeLock.WaitAsync(CancellationToken.None)</c>.
///
/// These tests are in CodingAgentWebUI.UnitTests so that coverlet attributes coverage to
/// <c>src/CodingAgentWebUI/Services/LoopStatePersistenceService.cs</c> (via the
/// [CodingAgentWebUI]* include in coverlet.runsettings).
/// </summary>
public class LoopStatePersistenceServicePersistTests : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _tempDir;
    private readonly string _stateFilePath;

    public LoopStatePersistenceServicePersistTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loop-persist-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _stateFilePath = Path.Combine(_tempDir, "loop-state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task OnLoopStateChanged_PersistsStateToStore()
    {
        // Arrange — use a controllable fake loop service that lets us raise OnChange directly
        var fakeLoop = new FakePipelineLoopService();
        var stateStore = new FileSystemLoopStateStore(_stateFilePath);
        using var sut = new LoopStatePersistenceService(fakeLoop, _logger, stateStore, TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Act — raise OnChange to trigger PersistCurrentStateAsync (fire-and-forget)
        // PersistCurrentStateAsync calls _writeLock.WaitAsync(CancellationToken.None)
        fakeLoop.RaiseOnChange();

        // Wait for the fire-and-forget persist to complete
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(_stateFilePath) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        // Assert — state file was written, confirming _writeLock.WaitAsync path was exercised
        File.Exists(_stateFilePath).Should().BeTrue("PersistCurrentStateAsync should write the state file");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnLoopStateChanged_WhenCalledMultipleTimes_WriteSerializesCorrectly()
    {
        // Arrange — verify the _writeLock serializes concurrent persist calls
        var fakeLoop = new FakePipelineLoopService();
        var stateStore = new FileSystemLoopStateStore(_stateFilePath);
        using var sut = new LoopStatePersistenceService(fakeLoop, _logger, stateStore, TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);

        // Act — fire 3 rapid state changes; _writeLock.WaitAsync(CancellationToken.None) serializes
        fakeLoop.RaiseOnChange();
        fakeLoop.RaiseOnChange();
        fakeLoop.RaiseOnChange();

        // Wait for all persists to complete
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(_stateFilePath) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(100); // Allow any in-progress concurrent writes to finish

        // Assert — file exists and contains valid JSON (no corruption from concurrent writes)
        File.Exists(_stateFilePath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(_stateFilePath);
        content.Should().NotBeNullOrEmpty();
        var act = () => System.Text.Json.JsonDocument.Parse(content);
        act.Should().NotThrow("concurrent persists serialized by _writeLock should not corrupt the file");

        await sut.StopAsync(CancellationToken.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal fake <see cref="IPipelineLoopService"/> that exposes a <see cref="RaiseOnChange"/>
    /// method for test control without requiring the full <see cref="PipelineLoopService"/>.
    /// </summary>
    private sealed class FakePipelineLoopService : IPipelineLoopService
    {
        public event Action? OnChange;
        public bool IsLoopActive { get; set; }
        public string StatusMessage => "idle";
        public string? CurrentIssueIdentifier => null;
        public int ProcessedCount => 0;
        public int FailedCount => 0;
        public int QueueCount => 0;
        public bool IsCircuitBroken => false;
        public string? LastPollError => null;
        public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses => new Dictionary<string, ConfigStatusSnapshot>();
        public int CurrentCycleTemplateIndex => 0;
        public int CurrentCycleTemplateCount => 0;
        public IReadOnlyList<string> ValidationErrors => Array.Empty<string>();

        public void RaiseOnChange() => OnChange?.Invoke();

        public Task<bool> StartLoopAsync() => Task.FromResult(true);
        public void StopLoop() { }
        public void ResumeLoop() { }
    }
}
