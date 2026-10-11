using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="LoopCommandExecutor"/> — verifies each command calls the correct
/// underlying methods and that config persistence rules are respected.
/// </summary>
public sealed class LoopCommandExecutorTests
{
    private readonly Mock<IPipelineLoopService> _loopService = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();

    private readonly LoopCommandExecutor _sut = new();

    public LoopCommandExecutorTests()
    {
        _loopService.Setup(l => l.ValidationErrors).Returns([]);
        _loopService.Setup(l => l.IsLoopActive).Returns(false);
        _configClient
            .Setup(c => c.UpdatePipelineConfigAsync(
                It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── ExecuteStartAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteStartAsync_WhenStartSucceeds_PersistsAutoStartTrueAndReturnsStarted()
    {
        _loopService.Setup(l => l.StartLoopAsync()).ReturnsAsync(true);

        var result = await _sut.ExecuteStartAsync(_loopService.Object, _configClient.Object, CancellationToken.None);

        result.Started.Should().BeTrue();
        result.Error.Should().BeNull();
        _loopService.Verify(l => l.StartLoopAsync(), Times.Once);
        _configClient.Verify(c => c.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once,
            "config must be persisted when start succeeds");
    }

    [Fact]
    public async Task ExecuteStartAsync_WhenStartFails_DoesNotPersistConfig()
    {
        _loopService.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        _loopService.Setup(l => l.IsLoopActive).Returns(false);

        var result = await _sut.ExecuteStartAsync(_loopService.Object, _configClient.Object, CancellationToken.None);

        result.Started.Should().BeFalse();
        result.Error.Should().NotBeNull();
        _configClient.Verify(c => c.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "config must NOT be persisted when start fails");
    }

    [Fact]
    public async Task ExecuteStartAsync_WhenAlreadyActive_ReturnsAlreadyActiveError()
    {
        _loopService.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        _loopService.Setup(l => l.IsLoopActive).Returns(true);

        var result = await _sut.ExecuteStartAsync(_loopService.Object, _configClient.Object, CancellationToken.None);

        result.Started.Should().BeFalse();
        result.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task ExecuteStartAsync_WhenValidationErrors_ReturnsValidationError()
    {
        _loopService.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        _loopService.Setup(l => l.ValidationErrors).Returns(["No templates"]);

        var result = await _sut.ExecuteStartAsync(_loopService.Object, _configClient.Object, CancellationToken.None);

        result.Error.Should().Contain("validation errors");
    }

    // ── ExecuteStopLoopOnlyAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteStopLoopOnlyAsync_CallsStopLoopAndDoesNotPersistConfig()
    {
        await _sut.ExecuteStopLoopOnlyAsync(_loopService.Object);

        _loopService.Verify(l => l.StopLoop(), Times.Once);
        _configClient.Verify(c => c.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "ExecuteStopLoopOnlyAsync must NOT persist config — that is ExecuteStopPersistAsync's job");
    }

    // ── ExecuteStopPersistAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteStopPersistAsync_PersistsAutoStartFalseAndDoesNotCallStopLoop()
    {
        PipelineConfiguration? capturedConfig = null;
        _configClient
            .Setup(c => c.UpdatePipelineConfigAsync(
                It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>(
                (transform, _) => capturedConfig = transform(new PipelineConfiguration { ClosedLoopAutoStart = true }))
            .Returns(Task.CompletedTask);

        await _sut.ExecuteStopPersistAsync(_configClient.Object, CancellationToken.None);

        _configClient.Verify(c => c.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        capturedConfig!.ClosedLoopAutoStart.Should().BeFalse(
            "ExecuteStopPersistAsync must set ClosedLoopAutoStart=false");
        _loopService.Verify(l => l.StopLoop(), Times.Never,
            "ExecuteStopPersistAsync must NOT call StopLoop — that is ExecuteStopLoopOnlyAsync's job");
    }

    // ── ExecuteResumeAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteResumeAsync_CallsResumeLoop()
    {
        await _sut.ExecuteResumeAsync(_loopService.Object);

        _loopService.Verify(l => l.ResumeLoop(), Times.Once);
    }
}
