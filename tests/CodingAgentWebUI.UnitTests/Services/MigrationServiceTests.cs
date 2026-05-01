using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for QualityGateMigrationService.
/// Tests startup migration logic for creating default configurations.
/// </summary>
public class MigrationServiceTests
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<ILogger> _mockLogger;

    public MigrationServiceTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockLogger = new Mock<ILogger>();
    }

    // ── QualityGateMigrationService ─────────────────────────────────────

    [Fact]
    public async Task QualityGateMigration_WhenNoQgcsExist_CreatesDefault()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>());
        _mockStore.Setup(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new QualityGateMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveQualityGateConfigAsync(
            It.Is<QualityGateConfiguration>(q =>
                q.DisplayName == "Default (migrated)" &&
                q.CompilationCommand == "dotnet" &&
                q.TestCommand == "dotnet" &&
                q.CoverageThreshold == 50.0 &&
                q.Enabled == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QualityGateMigration_WhenQgcsExist_SkipsMigration()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new() { DisplayName = "Existing" }
            });

        var service = new QualityGateMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QualityGateMigration_WhenStoreThrows_LogsErrorAndContinues()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var service = new QualityGateMigrationService(_mockStore.Object, _mockLogger.Object);

        // Should not throw — migration failure is non-fatal
        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QualityGateMigration_DefaultQgc_HasCorrectCompilationArguments()
    {
        QualityGateConfiguration? savedConfig = null;
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>());
        _mockStore.Setup(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<QualityGateConfiguration, CancellationToken>((q, _) => savedConfig = q)
            .Returns(Task.CompletedTask);

        var service = new QualityGateMigrationService(_mockStore.Object, _mockLogger.Object);
        await service.StartAsync(CancellationToken.None);

        savedConfig.Should().NotBeNull();
        savedConfig!.CompilationArguments.Should().BeEquivalentTo(new[] { "build", "--no-restore" });
        savedConfig.TestArguments.Should().BeEquivalentTo(new[] { "test", "--no-restore", "--no-build" });
        savedConfig.MatchLabels.Should().BeEmpty();
        savedConfig.ExecutionOrder.Should().Be(0);
    }

    [Fact]
    public async Task QualityGateMigration_StopAsync_CompletesImmediately()
    {
        var service = new QualityGateMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StopAsync(CancellationToken.None);

        // StopAsync is a no-op — just verify it doesn't throw
    }

    [Fact]
    public void QualityGateMigration_NullConfigStore_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QualityGateMigrationService(null!, _mockLogger.Object));
    }

    [Fact]
    public void QualityGateMigration_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QualityGateMigrationService(_mockStore.Object, null!));
    }
}
