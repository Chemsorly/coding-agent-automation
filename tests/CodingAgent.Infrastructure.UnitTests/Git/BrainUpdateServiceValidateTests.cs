using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for the real implementations of <see cref="BrainUpdateService.Validate"/> and
/// <see cref="BrainUpdateService.DetectChangesAsync"/>. All tests instantiate a real
/// <see cref="BrainUpdateService"/> backed by a <see cref="Mock{IGitOperations}"/> so the
/// method bodies are actually exercised rather than stubbed out.
/// </summary>
public class BrainUpdateServiceValidateTests : IDisposable
{
    private readonly Mock<IGitOperations> _mockGit;
    private readonly BrainUpdateService _sut;
    private readonly string _brainPath;

    private const string RunIdValue = "abc-123-run";
    private static readonly RunId TestRunId = new RunId(RunIdValue);

    public BrainUpdateServiceValidateTests()
    {
        _brainPath = Path.Combine(Path.GetTempPath(), $"brain-validate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_brainPath);

        _mockGit = new Mock<IGitOperations>();
        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), _mockGit.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_brainPath))
            Directory.Delete(_brainPath, recursive: true);
    }

    /// <summary>
    /// Writes a knowledge file at <paramref name="relativePath"/> under the temp brain directory.
    /// Creates any missing subdirectories.
    /// </summary>
    private void WriteKnowledgeFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_brainPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // ── Criterion (a): session log + log.md present → no warnings ───────────────

    /// <summary>
    /// Requirement (a): when changedFiles contains a sessions/{date}_{runId}.md path and
    /// "log.md", Validate returns a result with no warnings and all flags set to true.
    /// </summary>
    [Fact]
    public void Validate_BothSessionLogAndLogMdPresent_ReturnsNoWarnings()
    {
        var changedFiles = new[] { $"sessions/2024-01-01_{RunIdValue}.md", "log.md" };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.HasWarnings.Should().BeFalse();
        result.SessionLogCreated.Should().BeTrue();
        result.OperationLogUpdated.Should().BeTrue();
        result.EntryFormatValid.Should().BeTrue();
    }

    // ── Criterion (b): session log missing → warns about session log ─────────────

    /// <summary>
    /// Requirement (b): when no file in changedFiles matches sessions/{anything}/{runId},
    /// Validate adds "session log" to Warnings and sets SessionLogCreated to false.
    /// </summary>
    [Fact]
    public void Validate_SessionLogMissing_WarnsAboutSessionLog()
    {
        var changedFiles = new[] { "log.md" };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.SessionLogCreated.Should().BeFalse();
        result.Warnings.Should().Contain("session log");
    }

    /// <summary>
    /// Requirement (b) — run-ID specificity: a sessions/ file that does NOT contain the
    /// current run ID does not satisfy the session-log check. This falsifies the
    /// <c>f.Contains(runId.Value, StringComparison.OrdinalIgnoreCase)</c> guard and
    /// ensures it cannot be removed without breaking the test suite.
    /// </summary>
    [Fact]
    public void Validate_SessionLogForDifferentRunId_WarnsAboutSessionLog()
    {
        // The file is under sessions/ but belongs to a different run — not the current TestRunId
        var changedFiles = new[] { "sessions/2024-01-01_OTHER-RUN-XYZ.md", "log.md" };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.SessionLogCreated.Should().BeFalse(
            "a sessions/ file for a different run ID must not be counted as this run's session log");
        result.Warnings.Should().Contain("session log");
    }

    // ── Criterion (c): log.md missing → warns about log.md entry ────────────────

    /// <summary>
    /// Requirement (c): when no file in changedFiles matches "log.md" (exact or suffix),
    /// Validate adds "log.md entry" to Warnings and sets OperationLogUpdated to false.
    /// </summary>
    [Fact]
    public void Validate_LogMdMissing_WarnsAboutLogMdEntry()
    {
        var changedFiles = new[] { $"sessions/2024-01-01_{RunIdValue}.md" };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.OperationLogUpdated.Should().BeFalse();
        result.Warnings.Should().Contain("log.md entry");
    }

    // ── Criterion (d): knowledge file lacking YYYY-MM-DD date header → warns ────

    /// <summary>
    /// Requirement (d): when a knowledge .md file exists on disk and contains "###" but no
    /// "### YYYY-MM-DD" date header, Validate adds "proper entry format" to Warnings and
    /// sets EntryFormatValid to false.
    /// </summary>
    [Fact]
    public void Validate_KnowledgeFileWithInvalidDateHeader_WarnsAboutEntryFormat()
    {
        // Content has ### but not the required ### YYYY-MM-DD pattern
        WriteKnowledgeFile("general/dotnet.md", "### Some heading without a date\n- a lesson");

        var changedFiles = new[]
        {
            $"sessions/2024-01-01_{RunIdValue}.md",
            "log.md",
            "general/dotnet.md"
        };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.EntryFormatValid.Should().BeFalse();
        result.Warnings.Should().Contain("proper entry format");
    }

    /// <summary>
    /// Complement of (d): when the knowledge file contains a properly formatted
    /// "### YYYY-MM-DD" header, no format warning is issued.
    /// </summary>
    [Fact]
    public void Validate_KnowledgeFileWithValidDateHeader_NoFormatWarning()
    {
        // Content has ### YYYY-MM-DD — matches the DateHeaderRegex
        WriteKnowledgeFile("general/dotnet.md", "### 2024-01-15\n- a lesson with a valid date header");

        var changedFiles = new[]
        {
            $"sessions/2024-01-01_{RunIdValue}.md",
            "log.md",
            "general/dotnet.md"
        };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.EntryFormatValid.Should().BeTrue();
        result.Warnings.Should().NotContain("proper entry format");
    }

    // ── Criterion (e): DetectChangesAsync delegates to IGitOperations ────────────

    /// <summary>
    /// Requirement (e): DetectChangesAsync calls IGitOperations.GetChangedFiles with
    /// exactly the brainPath argument that was passed in, and returns its result unchanged.
    /// </summary>
    [Fact]
    public async Task DetectChangesAsync_InvokesGetChangedFilesWithBrainPath_ReturnsResult()
    {
        IReadOnlyList<string> expected = new[] { "log.md", "sessions/x.md" };
        _mockGit.Setup(g => g.GetChangedFiles(_brainPath)).Returns(expected);

        var result = await _sut.DetectChangesAsync(_brainPath, CancellationToken.None);

        _mockGit.Verify(g => g.GetChangedFiles(_brainPath), Times.Once);
        result.Should().Equal(expected);
    }

    // ── Additional branch coverage ────────────────────────────────────────────────

    /// <summary>
    /// Windows-style backslash paths in changedFiles are normalized by Replace('\\', '/') before
    /// the sessions/ check, so a session log written with backslashes is still recognized.
    /// </summary>
    [Fact]
    public void Validate_SessionLogPathWithBackslash_IsNormalized()
    {
        var changedFiles = new[] { $"sessions\\2024-01-01_{RunIdValue}.md", "log.md" };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.SessionLogCreated.Should().BeTrue(
            "backslash paths are normalized to forward slashes before the sessions/ check");
    }

    /// <summary>
    /// When changedFiles is empty, both "session log" and "log.md entry" warnings are produced.
    /// EntryFormatValid stays true because the knowledge-file loop has nothing to check.
    /// </summary>
    [Fact]
    public void Validate_EmptyChangedFiles_ProducesTwoWarnings()
    {
        var result = _sut.Validate(_brainPath, TestRunId, Array.Empty<string>());

        result.SessionLogCreated.Should().BeFalse();
        result.OperationLogUpdated.Should().BeFalse();
        result.Warnings.Should().Contain("session log");
        result.Warnings.Should().Contain("log.md entry");
        // no knowledge files → entry format check is skipped → stays valid
        result.EntryFormatValid.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
    }

    /// <summary>
    /// A knowledge .md file listed in changedFiles but not present on disk is skipped
    /// silently — no format warning is issued.
    /// </summary>
    [Fact]
    public void Validate_KnowledgeFileNotOnDisk_IsSkippedWithNoFormatWarning()
    {
        // "general/nonexistent.md" passes the knowledgeFiles filter but does not exist on disk
        var changedFiles = new[]
        {
            $"sessions/2024-01-01_{RunIdValue}.md",
            "log.md",
            "general/nonexistent.md"
        };

        var result = _sut.Validate(_brainPath, TestRunId, changedFiles);

        result.EntryFormatValid.Should().BeTrue(
            "knowledge files that do not exist on disk are skipped and do not trigger a format warning");
        result.HasWarnings.Should().BeFalse();
    }
}
