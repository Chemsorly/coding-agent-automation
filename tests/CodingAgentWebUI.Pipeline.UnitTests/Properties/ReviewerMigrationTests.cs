#pragma warning disable CS0618 // Obsolete — tests exercise legacy CodeReviewConfiguration.Agents field

using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for ReviewerMigrationService precondition gate.
/// Feature: 014-reviewer-configuration-ui, Property 4: Migration Precondition Gate
/// </summary>
public class ReviewerMigrationTests
{
    /// <summary>
    /// Property 4: Migration Precondition Gate
    /// Migration executes (SaveReviewerConfigAsync called) if and only if all three preconditions are met:
    /// (1) LoadReviewerConfigsAsync returns empty list (no existing reviewer configs)
    /// (2) LoadPipelineConfigAsync succeeds (pipeline-config.json exists)
    /// (3) CodeReviewConfiguration.Agents contains entries (non-null, non-empty)
    /// **Validates: Requirements 9.1, 9.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MigrationPreconditionArbitraries) })]
    public async void MigrationPreconditionGate_SaveCalledIffAllPreconditionsMet(MigrationPreconditionInput input)
    {
        // Arrange
        var mockConfigStore = new Mock<IConfigurationStore>();
        var mockLogger = new Mock<ILogger>();

        // Precondition 1: LoadReviewerConfigsAsync returns empty or non-empty list
        mockConfigStore
            .Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(input.ExistingReviewerConfigs);

        // Precondition 2: LoadPipelineConfigAsync returns config (or throws if pipeline-config.json missing)
        if (input.PipelineConfigExists)
        {
            mockConfigStore
                .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(input.PipelineConfig);
        }
        else
        {
            mockConfigStore
                .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new FileNotFoundException("pipeline-config.json not found"));
        }

        // Allow save to succeed
        mockConfigStore
            .Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ReviewerMigrationService(mockConfigStore.Object, mockLogger.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert: determine expected behavior
        var precondition1Met = input.ExistingReviewerConfigs.Count == 0;
        var precondition2Met = input.PipelineConfigExists;
        var precondition3Met = input.PipelineConfig.CodeReview.Agents is { Count: > 0 };
        var allPreconditionsMet = precondition1Met && precondition2Met && precondition3Met;

        if (allPreconditionsMet)
        {
            // Migration should execute — SaveReviewerConfigAsync called exactly once
            mockConfigStore.Verify(
                s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "SaveReviewerConfigAsync should be called exactly once when all preconditions are met");
        }
        else
        {
            // Migration should NOT execute — SaveReviewerConfigAsync never called
            mockConfigStore.Verify(
                s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()),
                Times.Never,
                $"SaveReviewerConfigAsync should NOT be called when preconditions not met " +
                $"(P1={precondition1Met}, P2={precondition2Met}, P3={precondition3Met})");
        }
    }
}

// --- Wrapper types for FsCheck ---

/// <summary>Input for migration precondition gate property tests.</summary>
public sealed class MigrationPreconditionInput
{
    /// <summary>Existing reviewer configs (empty = precondition 1 met).</summary>
    public required IReadOnlyList<ReviewerConfiguration> ExistingReviewerConfigs { get; init; }

    /// <summary>Whether pipeline-config.json exists (true = precondition 2 met).</summary>
    public required bool PipelineConfigExists { get; init; }

    /// <summary>The pipeline configuration (Agents populated/null/empty = precondition 3).</summary>
    public required PipelineConfiguration PipelineConfig { get; init; }

    public override string ToString()
    {
        var agentsDesc = PipelineConfig.CodeReview.Agents switch
        {
            null => "null",
            { Count: 0 } => "empty",
            { Count: var c } => $"{c} agents"
        };
        return $"ExistingConfigs={ExistingReviewerConfigs.Count}, PipelineExists={PipelineConfigExists}, Agents={agentsDesc}";
    }
}

// --- Arbitrary generators ---

public class MigrationPreconditionArbitraries
{
    private static readonly string[] AgentNamePool = ["Correctness", "Security", "DotNetSpecialist", "PythonLinter", "Performance"];
    private static readonly string[] PromptPool = ["Review for correctness", "Check security issues", "Verify .NET patterns", "Lint Python code", "Check performance"];

    public static Arbitrary<MigrationPreconditionInput> MigrationPreconditionInputArb()
    {
        // Generate existing reviewer configs: either empty list or non-empty list
        var emptyReviewerConfigs = Gen.Constant<IReadOnlyList<ReviewerConfiguration>>(
            Array.Empty<ReviewerConfiguration>());

        var nonEmptyReviewerConfigs =
            from count in Gen.Choose(1, 3)
            from configs in Gen.ArrayOf(
                from name in Gen.Elements("Existing Config 1", "Existing Config 2", "Existing Config 3")
                select new ReviewerConfiguration
                {
                    DisplayName = name,
                    MatchLabels = [],
                    Agents = new[] { new ReviewAgent { Name = "Agent", Prompt = "Prompt" } },
                    Enabled = true,
                    ExecutionOrder = 0
                }, count)
            select (IReadOnlyList<ReviewerConfiguration>)configs.ToList();

        var existingConfigsGen = Gen.OneOf(emptyReviewerConfigs, nonEmptyReviewerConfigs);

        // Generate pipeline config exists flag
        var pipelineExistsGen = Gen.Elements(true, false);

        // Generate pipeline config with various Agents states: null, empty, or populated
        var nullAgentsConfigGen = Gen.Constant(new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath(),
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = null }
        });

        var emptyAgentsConfigGen = Gen.Constant(new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath(),
            CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = Array.Empty<ReviewAgentConfig>() }
        });

        var populatedAgentsConfigGen =
            from agentCount in Gen.Choose(1, 4)
            from agents in Gen.ArrayOf(
                from name in Gen.Elements(AgentNamePool)
                from prompt in Gen.Elements(PromptPool)
                select new ReviewAgentConfig { Name = name, Prompt = prompt }, agentCount)
            select new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                CodeReview = new CodeReviewConfiguration { Enabled = true, Agents = agents.ToList() }
            };

        var pipelineConfigGen = Gen.OneOf(nullAgentsConfigGen, emptyAgentsConfigGen, populatedAgentsConfigGen);

        // Combine all inputs
        var inputGen =
            from existingConfigs in existingConfigsGen
            from pipelineExists in pipelineExistsGen
            from pipelineConfig in pipelineConfigGen
            select new MigrationPreconditionInput
            {
                ExistingReviewerConfigs = existingConfigs,
                PipelineConfigExists = pipelineExists,
                PipelineConfig = pipelineConfig
            };

        return inputGen.ToArbitrary();
    }
}
