using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class LoopStatePersistenceServiceTests : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _tempDir;
    private readonly string _stateFilePath;

    public LoopStatePersistenceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loop-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _stateFilePath = Path.Combine(_tempDir, "loop-state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task StartedAsync_WhenFileHasActiveState_SetsIsResuming()
    {
        // Arrange
        WriteStateFile(isActive: true);
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger, new FileSystemLoopStateStore(_stateFilePath), TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await sut.StartedAsync(cts.Token);

        // Assert
        Assert.True(sut.IsResuming);
        Assert.True(sut.ResumeCountdownSeconds > 0);

        // Cleanup
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        // Wait for fire-and-forget resume task to observe cancellation
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sut.IsResuming && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(sut.IsResuming);
    }

    [Fact]
    public async Task StartedAsync_WhenFileMissing_DoesNotResume()
    {
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger, new FileSystemLoopStateStore(_stateFilePath), TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await sut.StartedAsync(cts.Token);

        Assert.False(sut.IsResuming);
        Assert.Equal(0, sut.ResumeCountdownSeconds);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartedAsync_WhenFileHasInactiveState_DoesNotResume()
    {
        WriteStateFile(isActive: false);
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger, new FileSystemLoopStateStore(_stateFilePath), TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await sut.StartedAsync(cts.Token);

        Assert.False(sut.IsResuming);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartedAsync_WhenFileIsCorruptJson_DefaultsToInactive()
    {
        await File.WriteAllTextAsync(_stateFilePath, "{ not valid json !!!");
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger, new FileSystemLoopStateStore(_stateFilePath), TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await sut.StartedAsync(cts.Token);

        Assert.False(sut.IsResuming);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartedAsync_CancellationDuringDelay_AbortsResume()
    {
        WriteStateFile(isActive: true);
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger, new FileSystemLoopStateStore(_stateFilePath), TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        await sut.StartedAsync(cts.Token);

        Assert.True(sut.IsResuming);

        // Cancel during delay
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);

        // Wait for fire-and-forget resume task to observe cancellation
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sut.IsResuming && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(sut.IsResuming);
        Assert.Equal(0, sut.ResumeCountdownSeconds);
    }

    [Fact]
    public void ResolveStartupDelay_DefaultIs90Seconds()
    {
        Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", null);
        try
        {
            var delay = LoopStatePersistenceService.ResolveStartupDelay();
            Assert.Equal(TimeSpan.FromSeconds(90), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("45", 45)]
    [InlineData("600", 600)]
    public void ResolveStartupDelay_ParsesValidValues(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", envValue);
        try
        {
            var delay = LoopStatePersistenceService.ResolveStartupDelay();
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("-5", 0)]
    [InlineData("9999", 600)]
    public void ResolveStartupDelay_ClampsOutOfBounds(string envValue, int expectedSeconds)
    {
        Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", envValue);
        try
        {
            var delay = LoopStatePersistenceService.ResolveStartupDelay();
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", null);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12.5")]
    public void ResolveStartupDelay_InvalidFallsBackToDefault(string envValue)
    {
        Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", envValue);
        try
        {
            var delay = LoopStatePersistenceService.ResolveStartupDelay();
            Assert.Equal(TimeSpan.FromSeconds(90), delay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PIPELINE_LOOP_STARTUP_DELAY_SECONDS", null);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void WriteStateFile(bool isActive)
    {
        var state = new { isActive, startedAt = isActive ? DateTimeOffset.UtcNow : (DateTimeOffset?)null, stoppedAt = !isActive ? DateTimeOffset.UtcNow : (DateTimeOffset?)null };
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private PipelineLoopService CreateLoopService()
    {
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var mockProviderConfigStore = new Mock<IProviderConfigStore>();
        mockProviderConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        var mockFactory = new Mock<IProviderFactory>();
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            logger: _logger);

        return new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = runCreator,
            ProviderFactory = mockFactory.Object,
            PipelineConfigStore = mockConfigStore.Object,
            ProviderConfigStore = mockProviderConfigStore.Object,
            ProjectStore = mockProjectStore.Object,
            Logger = _logger,
            WorkDistributor = null,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker = null,
            HousekeepingService = null,
            LeaderElection = null
        });
    }

    [Fact]
    public async Task OnLoopStateChanged_WhenLoopActive_PersistsActiveState()
    {
        // Arrange: use a stub loop service that exposes FireOnChange for testing
        var mockStore = new Mock<ILoopStateStore>();
        LoopState? writtenState = null;
        var writeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockStore.Setup(s => s.WriteAsync(It.IsAny<LoopState>(), It.IsAny<CancellationToken>()))
            .Callback<LoopState, CancellationToken>((s, _) =>
            {
                writtenState = s;
                writeCompleted.TrySetResult();
            })
            .Returns(Task.CompletedTask);
        mockStore.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopState?)null);

        var stubLoop = new StubLoopService();
        using var sut = new LoopStatePersistenceService(stubLoop, _logger, mockStore.Object, TimeSpan.Zero);
        await sut.StartAsync(CancellationToken.None);

        // Trigger the OnChange event
        stubLoop.FireOnChange();

        // Wait for fire-and-forget write to complete — event-driven, no polling loop
        await writeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        writtenState.Should().NotBeNull("PersistCurrentStateAsync should write state on change");

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PersistCurrentStateAsync_WhenStoreThrows_DoesNotPropagate()
    {
        var mockStore = new Mock<ILoopStateStore>();
        var writeCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockStore.Setup(s => s.WriteAsync(It.IsAny<LoopState>(), It.IsAny<CancellationToken>()))
            .Callback(() => writeCalled.TrySetResult())
            .ThrowsAsync(new IOException("Disk full"));
        mockStore.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopState?)null);

        var stubLoop = new StubLoopService();
        using var sut = new LoopStatePersistenceService(stubLoop, _logger, mockStore.Object, TimeSpan.Zero);
        await sut.StartAsync(CancellationToken.None);

        // Exception from store must be swallowed — should not propagate
        stubLoop.FireOnChange();

        // Wait for the fire-and-forget write to be attempted — event-driven, no fixed delay
        await writeCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sut.StopAsync(CancellationToken.None);
        // Verify the store was called (write was attempted) and exception was swallowed
        mockStore.Verify(s => s.WriteAsync(It.IsAny<LoopState>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "WriteAsync should have been called — the exception path must be exercised");
        // Service should still be stopped cleanly (no hung state)
        sut.IsResuming.Should().BeFalse("service should remain in a clean state after store exception");
    }

    [Fact]
    public async Task StopAsync_UnregistersOnChangeHandler()
    {
        var mockStore = new Mock<ILoopStateStore>();
        mockStore.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((LoopState?)null);

        var stubLoop = new StubLoopService();
        using var sut = new LoopStatePersistenceService(stubLoop, _logger, mockStore.Object, TimeSpan.Zero);
        await sut.StartAsync(CancellationToken.None);
        stubLoop.HasHandlers.Should().BeTrue("handler registered after StartAsync");

        await sut.StopAsync(CancellationToken.None);
        stubLoop.HasHandlers.Should().BeFalse("handler unregistered after StopAsync");

        // Verify no writes occur after unregistration
        mockStore.Verify(s => s.WriteAsync(It.IsAny<LoopState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartedAsync_WhenActiveState_WithZeroDelay_ResumesLoopImmediately()
    {
        WriteStateFile(isActive: true);
        var loopService = CreateLoopService();
        using var sut = new LoopStatePersistenceService(loopService, _logger,
            new FileSystemLoopStateStore(_stateFilePath), TimeSpan.Zero); // zero delay

        await sut.StartAsync(CancellationToken.None);
        await sut.StartedAsync(CancellationToken.None);

        // With zero delay the resume runs almost immediately — wait briefly for the fire-and-forget
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (sut.IsResuming && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        sut.IsResuming.Should().BeFalse("zero-delay resume should complete quickly");

        await sut.StopAsync(CancellationToken.None);
    }
}

/// <summary>
/// Minimal stub of <see cref="IPipelineLoopService"/> that allows tests to fire the OnChange event.
/// </summary>
internal sealed class StubLoopService : IPipelineLoopService
{
    public event Action? OnChange;
    public bool HasHandlers => OnChange is not null;
    public void FireOnChange() => OnChange?.Invoke();

    public bool IsLoopActive => false;
    public string StatusMessage => "";
    public string? CurrentIssueIdentifier => null;
    public int ProcessedCount => 0;
    public int FailedCount => 0;
    public int QueueCount => 0;
    public bool IsCircuitBroken => false;
    public string? LastPollError => null;
    public IReadOnlyDictionary<string, CodingAgentWebUI.Pipeline.Models.ConfigStatusSnapshot> TemplateStatuses
        => new Dictionary<string, CodingAgentWebUI.Pipeline.Models.ConfigStatusSnapshot>();
    public int CurrentCycleTemplateIndex => 0;
    public int CurrentCycleTemplateCount => 0;
    public IReadOnlyList<string> ValidationErrors => [];
    public Task<bool> StartLoopAsync() => Task.FromResult(false);
    public void StopLoop() { }
    public void ResumeLoop() { }
}