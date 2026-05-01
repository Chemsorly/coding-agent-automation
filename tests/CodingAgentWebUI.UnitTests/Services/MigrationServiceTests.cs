using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for QualityGateMigrationService and ReviewerMigrationService.
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

    // ── ReviewerMigrationService ────────────────────────────────────────

    [Fact]
    public async Task ReviewerMigration_WhenNoConfigsExist_AndLegacyAgentsPopulated_CreatesMigratedConfig()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

        #pragma warning disable CS0618
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    Agents = new List<ReviewAgentConfig>
                    {
                        new() { Name = "Correctness", Prompt = "Review for correctness" },
                        new() { Name = "Security", Prompt = "Review for security" }
                    }
                }
            });
        #pragma warning restore CS0618

        _mockStore.Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveReviewerConfigAsync(
            It.Is<ReviewerConfiguration>(r =>
                r.DisplayName == "Default Reviewers (Migrated)" &&
                r.Agents.Count == 2 &&
                r.Agents[0].Name == "Correctness" &&
                r.Agents[1].Name == "Security" &&
                r.Enabled == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReviewerMigration_WhenConfigsAlreadyExist_SkipsMigration()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new() { DisplayName = "Existing", Agents = new List<ReviewAgent> { new() { Name = "A", Prompt = "P" } } }
            });

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockStore.Verify(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewerMigration_WhenLegacyAgentsEmpty_SkipsMigration()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

        #pragma warning disable CS0618
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    Agents = Array.Empty<ReviewAgentConfig>()
                }
            });
        #pragma warning restore CS0618

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewerMigration_WhenLegacyAgentsNull_SkipsMigration()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

        #pragma warning disable CS0618
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    Agents = null
                }
            });
        #pragma warning restore CS0618

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewerMigration_WhenStoreThrows_LogsErrorAndContinues()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        // Should not throw — migration failure is non-fatal
        await service.StartAsync(CancellationToken.None);

        _mockStore.Verify(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReviewerMigration_MigratedConfig_HasCorrectDefaults()
    {
        ReviewerConfiguration? savedConfig = null;
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());

        #pragma warning disable CS0618
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    Agents = new List<ReviewAgentConfig>
                    {
                        new() { Name = "Agent1", Prompt = "Prompt1" }
                    }
                }
            });
        #pragma warning restore CS0618

        _mockStore.Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<ReviewerConfiguration, CancellationToken>((r, _) => savedConfig = r)
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);
        await service.StartAsync(CancellationToken.None);

        savedConfig.Should().NotBeNull();
        savedConfig!.MatchLabels.Should().BeEmpty();
        savedConfig.ExecutionOrder.Should().Be(0);
        savedConfig.Enabled.Should().BeTrue();
        savedConfig.Agents[0].Name.Should().Be("Agent1");
        savedConfig.Agents[0].Prompt.Should().Be("Prompt1");
    }

    [Fact]
    public async Task ReviewerMigration_StopAsync_CompletesImmediately()
    {
        var service = new ReviewerMigrationService(_mockStore.Object, _mockLogger.Object);

        await service.StopAsync(CancellationToken.None);

        // StopAsync is a no-op — just verify it doesn't throw
    }

    [Fact]
    public void ReviewerMigration_NullConfigStore_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReviewerMigrationService(null!, _mockLogger.Object));
    }

    [Fact]
    public void ReviewerMigration_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReviewerMigrationService(_mockStore.Object, null!));
    }
}
