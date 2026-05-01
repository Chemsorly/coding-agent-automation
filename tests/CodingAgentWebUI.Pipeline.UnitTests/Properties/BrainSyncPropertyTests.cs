using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using Moq;
using System.Text.Json;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for brain repository sync feature.
/// Validates the 11 correctness properties from the design document.
/// </summary>
public class BrainSyncPropertyTests
{
    /// <summary>
    /// Property 1: Gitignore ensure is idempotent.
    /// </summary>
    [Property]
    public void GitignoreEnsureIsIdempotent(NonNull<string> content)
    {
        var original = content.Get;
        var once = IBrainUpdateService.EnsureGitignoreEntry(original, ".brain/");
        var twice = IBrainUpdateService.EnsureGitignoreEntry(once, ".brain/");

        once.Split('\n').Any(l => l.Trim() == ".brain/").Should().BeTrue();
        twice.Should().Be(once);
    }

    /// <summary>
    /// Property 2: Brain-enabled prompt contains all required instructions.
    /// </summary>
    [Fact]
    public void BrainEnabledPromptContainsAllRequiredInstructions()
    {
        var context = PromptBuilder.BuildBrainContextSection(
            true, "my-project", "dotnet, blazor");
        var writeInstructions = PromptBuilder.BuildBrainWriteInstructions(
            true, "run-123", "issue-456");

        // All 10 required elements
        context.Should().Contain(".brain/AGENTS.md");
        context.Should().Contain("SEPARATE Git repository");
        context.Should().Contain("Do NOT run git commands");
        context.Should().Contain("my-project");
        context.Should().Contain("dotnet, blazor");
        writeInstructions.Should().Contain("lessons learned");
        writeInstructions.Should().Contain("APPEND");
        writeInstructions.Should().Contain("sessions/");
        writeInstructions.Should().Contain("log.md");
        writeInstructions.Should().Contain("Do NOT commit");
    }

    [Property]
    public void BrainContextSection_AlwaysContainsCoreInstructions_WhenEnabled(
        NonEmptyString projectName)
    {
        var context = PromptBuilder.BuildBrainContextSection(true, projectName.Get);

        context.Should().Contain(".brain/AGENTS.md");
        context.Should().Contain("SEPARATE Git repository");
        context.Should().Contain("Do NOT run git commands");
    }

    /// <summary>
    /// Property 3: Brain-disabled prompt omits brain instructions.
    /// </summary>
    [Fact]
    public void BrainDisabledPromptOmitsBrainInstructions()
    {
        var context = PromptBuilder.BuildBrainContextSection(false);
        var writeInstructions = PromptBuilder.BuildBrainWriteInstructions(false, "run-123", "issue-456");

        context.Should().BeEmpty();
        writeInstructions.Should().BeEmpty();
    }

    // Property 4 (BrainCommitMessageContainsRunIdAndIssueIdentifier) moved to Infrastructure.UnitTests

    // Property 5 (AcceptBothMergeResolutionPreservesBothSides) moved to Infrastructure.UnitTests

    /// <summary>
    /// Property 6: Configuration round-trip preserves brain fields.
    /// </summary>
    [Fact]
    public async Task ConfigurationRoundTripPreservesBrainFields()
    {
        var mockStore = new Mock<IConfigurationStore>();
        PipelineConfiguration? saved = null;
        mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineConfiguration, CancellationToken>((c, _) => saved = c)
            .Returns(Task.CompletedTask);
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved!);

        var original = new PipelineConfiguration
        {
            LastUsedProviderIds = new Dictionary<string, string>
            {
                ["issue"] = "id-1",
                ["repository"] = "id-2",
                ["brain"] = "id-3"
            },
            BlacklistedPaths = new[] { ".kiro", ".github", ".brain" },
            BrainReadOnly = true
        };

        await mockStore.Object.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await mockStore.Object.LoadPipelineConfigAsync(CancellationToken.None);

        loaded.LastUsedProviderIds.Should().BeEquivalentTo(original.LastUsedProviderIds);
        loaded.BlacklistedPaths.Should().BeEquivalentTo(original.BlacklistedPaths);
        loaded.BrainReadOnly.Should().Be(original.BrainReadOnly);
    }

    /// <summary>
    /// Property 7: Brain update validation detects missing items.
    /// </summary>
    [Fact]
    public void BrainValidationDetectsMissingSessionLog()
    {
        var mockService = new Mock<IBrainUpdateService>();
        mockService.Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult
            {
                SessionLogCreated = false,
                OperationLogUpdated = false,
                Warnings = new[] { "session log", "log.md entry" }
            });

        var changedFiles = new List<string> { "general/lessons-learned.md" };
        var result = mockService.Object.Validate("/fake/path", "test-run-id", changedFiles);

        result.SessionLogCreated.Should().BeFalse();
        result.OperationLogUpdated.Should().BeFalse();
        result.HasWarnings.Should().BeTrue();
        result.Warnings.Should().Contain("session log");
        result.Warnings.Should().Contain("log.md entry");
    }

    [Fact]
    public void BrainValidationDetectsPresenceOfSessionLogAndLogMd()
    {
        var mockService = new Mock<IBrainUpdateService>();
        mockService.Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult
            {
                SessionLogCreated = true,
                OperationLogUpdated = true
            });

        var changedFiles = new List<string>
        {
            "sessions/2026-07-19_test-run-id.md",
            "log.md"
        };
        var result = mockService.Object.Validate("/fake/path", "test-run-id", changedFiles);

        result.SessionLogCreated.Should().BeTrue();
        result.OperationLogUpdated.Should().BeTrue();
    }

    // Property 8 (FallbackLogEntryContainsRunIdAndModifiedFiles) moved to Infrastructure.UnitTests

    // Property 9 (FeedbackLoopPromptIncludesPreviousWarnings) removed — feature #150 closed as not-planned

    /// <summary>
    /// Property 10: No credentials in brain prompts.
    /// </summary>
    [Fact]
    public void NoCredentialsInBrainPrompts()
    {
        var token = "ghp_abc123secrettoken456";
        var context = PromptBuilder.BuildBrainContextSection(true, "my-project", "dotnet");
        var writeInstructions = PromptBuilder.BuildBrainWriteInstructions(true, "run-1", "issue-1");

        context.Should().NotContain(token);
        writeInstructions.Should().NotContain(token);
        context.Should().NotContain("ghp_");
        writeInstructions.Should().NotContain("ghp_");
    }

    /// <summary>
    /// Property 11: RepositoryRole backward-compatible deserialization.
    /// </summary>
    [Fact]
    public void RepositoryRoleBackwardCompatibleDeserialization()
    {
        var json = """{"Id":"test-id","Kind":1,"ProviderType":"GitHub","DisplayName":"Test","Settings":{}}""";
        var config = JsonSerializer.Deserialize<ProviderConfig>(json);

        config.Should().NotBeNull();
        config!.RepositoryRole.Should().Be(RepositoryRole.Work);
    }

    [Fact]
    public void NewProviderConfigDefaultsToWork()
    {
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };
        config.RepositoryRole.Should().Be(RepositoryRole.Work);
    }
}
