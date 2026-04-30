#pragma warning disable CS0618 // Obsolete — tests exercise legacy CodeReviewConfiguration.Agents field

using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="ReviewerMigrationService"/>.
/// Validates migration behavior: correct default config creation, agent mapping,
/// non-modification of original data, and precondition skip scenarios.
/// Requirements: 9.1, 9.2, 9.3, 9.4
/// </summary>
public class ReviewerMigrationServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<ILogger> _mockLogger;

    public ReviewerMigrationServiceTests()
    {
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockLogger = new Mock<ILogger>();
    }

    /// <summary>
    /// Test 1: Migration creates correct default config with expected field values.
    /// DisplayName="Default Reviewers (Migrated)", MatchLabels=[], Enabled=true, ExecutionOrder=0
    /// Requirements: 9.1, 9.3
    /// </summary>
    [Fact]
    public async Task StartAsync_AllPreconditionsMet_CreatesConfigWithCorrectDefaults()
    {
        // Arrange
        var legacyAgents = new List<ReviewAgentConfig>
        {
            new() { Name = "Correctness", Prompt = "Review for correctness" }
        };

        var pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = legacyAgents }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        ReviewerConfiguration? savedConfig = null;
        _mockConfigStore
            .Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<ReviewerConfiguration, CancellationToken>((config, _) => savedConfig = config)
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.DisplayName.Should().Be("Default Reviewers (Migrated)");
        savedConfig.MatchLabels.Should().BeEmpty();
        savedConfig.Enabled.Should().BeTrue();
        savedConfig.ExecutionOrder.Should().Be(0);
        savedConfig.Id.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(savedConfig.Id, out _).Should().BeTrue("Id should be a valid GUID");
    }

    /// <summary>
    /// Test 2: Migration maps ReviewAgentConfig → ReviewAgent correctly (Name and Prompt preserved).
    /// Requirements: 9.3
    /// </summary>
    [Fact]
    public async Task StartAsync_AllPreconditionsMet_MapsAgentsCorrectly()
    {
        // Arrange
        var legacyAgents = new List<ReviewAgentConfig>
        {
            new() { Name = "Correctness", Prompt = "Review for correctness issues" },
            new() { Name = "SecurityReviewer", Prompt = "Review for security vulnerabilities" },
            new() { Name = "DotNetSpecialist", Prompt = "Review for .NET-specific patterns" }
        };

        var pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = legacyAgents }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        ReviewerConfiguration? savedConfig = null;
        _mockConfigStore
            .Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<ReviewerConfiguration, CancellationToken>((config, _) => savedConfig = config)
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.Agents.Should().HaveCount(3);

        savedConfig.Agents[0].Name.Should().Be("Correctness");
        savedConfig.Agents[0].Prompt.Should().Be("Review for correctness issues");

        savedConfig.Agents[1].Name.Should().Be("SecurityReviewer");
        savedConfig.Agents[1].Prompt.Should().Be("Review for security vulnerabilities");

        savedConfig.Agents[2].Name.Should().Be("DotNetSpecialist");
        savedConfig.Agents[2].Prompt.Should().Be("Review for .NET-specific patterns");
    }

    /// <summary>
    /// Test 3: Migration does not modify original CodeReviewConfiguration.Agents.
    /// Requirements: 9.4
    /// </summary>
    [Fact]
    public async Task StartAsync_AllPreconditionsMet_DoesNotModifyOriginalAgents()
    {
        // Arrange
        var legacyAgents = new List<ReviewAgentConfig>
        {
            new() { Name = "Correctness", Prompt = "Review for correctness" },
            new() { Name = "Security", Prompt = "Review for security" }
        };

        var pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = legacyAgents }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        _mockConfigStore
            .Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — original agents list is unchanged
        pipelineConfig.CodeReview.Agents.Should().HaveCount(2);
        pipelineConfig.CodeReview.Agents![0].Name.Should().Be("Correctness");
        pipelineConfig.CodeReview.Agents[0].Prompt.Should().Be("Review for correctness");
        pipelineConfig.CodeReview.Agents[1].Name.Should().Be("Security");
        pipelineConfig.CodeReview.Agents[1].Prompt.Should().Be("Review for security");
    }

    /// <summary>
    /// Test 4: Migration skips when LoadReviewerConfigsAsync returns non-empty list.
    /// SaveReviewerConfigAsync should NOT be called.
    /// Requirements: 9.2
    /// </summary>
    [Fact]
    public async Task StartAsync_ExistingReviewerConfigs_SkipsMigration()
    {
        // Arrange
        var existingConfigs = new List<ReviewerConfiguration>
        {
            new()
            {
                DisplayName = "Existing Config",
                MatchLabels = [],
                Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Prompt1" } },
                Enabled = true,
                ExecutionOrder = 0
            }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingConfigs);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — SaveReviewerConfigAsync never called
        _mockConfigStore.Verify(
            s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // LoadPipelineConfigAsync should not even be called (short-circuit)
        _mockConfigStore.Verify(
            s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Test 5: Migration skips when LoadPipelineConfigAsync returns config with null Agents.
    /// SaveReviewerConfigAsync should NOT be called.
    /// Requirements: 9.2
    /// </summary>
    [Fact]
    public async Task StartAsync_NullAgents_SkipsMigration()
    {
        // Arrange
        var pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = null }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — SaveReviewerConfigAsync never called
        _mockConfigStore.Verify(
            s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Test 5b: Migration skips when LoadPipelineConfigAsync returns config with empty Agents list.
    /// SaveReviewerConfigAsync should NOT be called.
    /// Requirements: 9.2
    /// </summary>
    [Fact]
    public async Task StartAsync_EmptyAgents_SkipsMigration()
    {
        // Arrange
        var pipelineConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = Array.Empty<ReviewAgentConfig>() }
        };

        _mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineConfig);

        var service = new ReviewerMigrationService(_mockConfigStore.Object, _mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert — SaveReviewerConfigAsync never called
        _mockConfigStore.Verify(
            s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
