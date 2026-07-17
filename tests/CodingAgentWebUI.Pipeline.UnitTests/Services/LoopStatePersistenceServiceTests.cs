using System.Text.Json;
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

        var orchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            logger: _logger);

        return new PipelineLoopService(
            orchestration,
            mockFactory.Object,
            mockConfigStore.Object,
            mockProviderConfigStore.Object,
            mockProjectStore.Object,
            _logger);
    }
}
