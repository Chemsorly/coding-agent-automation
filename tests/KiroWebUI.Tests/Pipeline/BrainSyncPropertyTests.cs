using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;
using KiroWebUI.Pipeline.Services;
using System.Text.Json;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for brain repository sync feature.
/// Validates the 11 correctness properties from the design document.
/// </summary>
public class BrainSyncPropertyTests
{
    /// <summary>
    /// Property 1: Gitignore ensure is idempotent.
    /// </summary>
    [Property(MaxTest = 100)]
    public void GitignoreEnsureIsIdempotent(NonNull<string> content)
    {
        var original = content.Get;
        var once = BrainUpdateService.EnsureGitignoreEntry(original, ".brain/");
        var twice = BrainUpdateService.EnsureGitignoreEntry(once, ".brain/");

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

    [Property(MaxTest = 100)]
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

    /// <summary>
    /// Property 4: Brain commit message contains run ID and issue identifier.
    /// </summary>
    [Property(MaxTest = 100)]
    public void BrainCommitMessageContainsRunIdAndIssueIdentifier(
        NonEmptyString runId, NonEmptyString issueId)
    {
        var message = BrainUpdateService.BuildCommitMessage(runId.Get, issueId.Get);
        message.Should().Contain(runId.Get);
        message.Should().Contain(issueId.Get);
    }

    /// <summary>
    /// Property 5: Accept-both merge resolution preserves both sides.
    /// </summary>
    [Property(MaxTest = 100)]
    public void AcceptBothMergeResolutionPreservesBothSides(
        NonEmptyString ours, NonEmptyString theirs)
    {
        var resolved = BrainUpdateService.ResolveConflictAcceptBoth(ours.Get, theirs.Get);
        resolved.Should().Contain(ours.Get);
        resolved.Should().Contain(theirs.Get);
    }

    /// <summary>
    /// Property 6: Configuration round-trip preserves brain fields.
    /// </summary>
    [Fact]
    public async Task ConfigurationRoundTripPreservesBrainFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonConfigurationStore(tempDir);
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

            await store.SavePipelineConfigAsync(original, CancellationToken.None);
            var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);

            loaded.LastUsedProviderIds.Should().BeEquivalentTo(original.LastUsedProviderIds);
            loaded.BlacklistedPaths.Should().BeEquivalentTo(original.BlacklistedPaths);
            loaded.BrainReadOnly.Should().Be(original.BrainReadOnly);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Property 7: Brain update validation detects missing items.
    /// </summary>
    [Fact]
    public void BrainValidationDetectsMissingSessionLog()
    {
        var logger = new Moq.Mock<Serilog.ILogger>();
        var service = new BrainUpdateService(logger.Object);

        var changedFiles = new List<string> { "general/lessons-learned.md" };
        var result = service.Validate("/fake/path", "test-run-id", changedFiles);

        result.SessionLogCreated.Should().BeFalse();
        result.OperationLogUpdated.Should().BeFalse();
        result.HasWarnings.Should().BeTrue();
        result.Warnings.Should().Contain("session log");
        result.Warnings.Should().Contain("log.md entry");
    }

    [Fact]
    public void BrainValidationDetectsPresenceOfSessionLogAndLogMd()
    {
        var logger = new Moq.Mock<Serilog.ILogger>();
        var service = new BrainUpdateService(logger.Object);

        var changedFiles = new List<string>
        {
            "sessions/2026-07-19_test-run-id.md",
            "log.md"
        };
        var result = service.Validate("/fake/path", "test-run-id", changedFiles);

        result.SessionLogCreated.Should().BeTrue();
        result.OperationLogUpdated.Should().BeTrue();
    }

    /// <summary>
    /// Property 8: Fallback log.md entry contains run ID and modified files.
    /// </summary>
    [Property(MaxTest = 100)]
    public void FallbackLogEntryContainsRunIdAndModifiedFiles(
        NonEmptyString runId, NonEmptyString file1, NonEmptyString file2)
    {
        var fileList = new List<string> { file1.Get, file2.Get };
        var entry = BrainUpdateService.BuildFallbackLogEntry(
            runId.Get, "2026-07-19", fileList);

        entry.Should().Contain(runId.Get);
        entry.Should().Contain(file1.Get);
        entry.Should().Contain(file2.Get);
    }

    /// <summary>
    /// Property 9: Feedback loop prompt includes previous warnings.
    /// </summary>
    [Fact]
    public void FeedbackLoopPromptIncludesPreviousWarnings()
    {
        var warnings = new List<string> { "session log", "log.md entry", "proper entry format" };
        var context = PromptBuilder.BuildBrainContextSection(true, previousWarnings: warnings);

        context.Should().Contain("session log");
        context.Should().Contain("log.md entry");
        context.Should().Contain("proper entry format");
        context.Should().Contain("previous brain repo update was missing");
    }

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
