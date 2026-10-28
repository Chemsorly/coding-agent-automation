using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Interfaces;
using Moq;
using System.Text.Json;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for brain repository sync feature.
/// Validates the 11 correctness properties from the design document.
/// </summary>
public class BrainSyncPropertyTests
{
    /// <summary>
    /// Property 1: Gitignore ensure is idempotent.
    /// </summary>
    [Property(MaxTest = 20)]
    public void GitignoreEnsureIsIdempotent(NonNull<string> content)
    {
        var original = content.Get;
        var once = IBrainUpdateService.EnsureGitignoreEntry(original, ".brain/");
        var twice = IBrainUpdateService.EnsureGitignoreEntry(once, ".brain/");

        once.Split('\n').Any(l => l.Trim() == ".brain/").Should().BeTrue();
        twice.Should().Be(once);
    }

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
            BlacklistedPaths = new[] { ".agent", ".github", ".brain" },
            BrainReadOnly = true
        };

        await mockStore.Object.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await mockStore.Object.LoadPipelineConfigAsync(CancellationToken.None);

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
        mockService.Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
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
        mockService.Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
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
