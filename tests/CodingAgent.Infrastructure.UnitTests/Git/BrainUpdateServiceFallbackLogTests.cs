using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="BrainUpdateService.BuildFallbackLogEntry"/> and
/// <see cref="BrainUpdateService.AppendFallbackLogEntryAsync"/>.
/// These methods are the fallback path that writes a log entry to log.md when the
/// consolidation agent does not produce one itself.
/// </summary>
public sealed class BrainUpdateServiceFallbackLogTests : IDisposable
{
    private readonly Serilog.Core.Logger _logger;
    private readonly BrainUpdateService _sut;
    private readonly string _tempDir;

    public BrainUpdateServiceFallbackLogTests()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _sut = new BrainUpdateService(_logger);
        _tempDir = Path.Combine(Path.GetTempPath(), $"brain-fallback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    // ── BuildFallbackLogEntry ───────────────────────────────────────────────────

    /// <summary>
    /// Non-empty file list produces the expected pipe-delimited format:
    /// "- {date} | {runId} | auto-fallback | Files: {file1}, {file2}"
    /// </summary>
    [Fact]
    public void BuildFallbackLogEntry_NonEmptyFileList_ReturnsPipeDelimitedFormat()
    {
        var result = BrainUpdateService.BuildFallbackLogEntry(
            "run-abc", "2026-10-31", new[] { "file1.md", "file2.md" });

        result.Should().Be(
            $"- 2026-10-31 | run-abc | auto-fallback | Files: file1.md, file2.md{Environment.NewLine}");
    }

    /// <summary>
    /// Empty file list produces a well-formed entry with an empty Files section —
    /// string.Join returns "" rather than throwing.
    /// </summary>
    [Fact]
    public void BuildFallbackLogEntry_EmptyFileList_ReturnsWellFormedEntry()
    {
        var result = BrainUpdateService.BuildFallbackLogEntry(
            "run-abc", "2026-10-31", Array.Empty<string>());

        result.Should().Be(
            $"- 2026-10-31 | run-abc | auto-fallback | Files: {Environment.NewLine}");
    }

    // ── AppendFallbackLogEntryAsync ─────────────────────────────────────────────

    /// <summary>
    /// When log.md does not exist, AppendFallbackLogEntryAsync creates it and writes
    /// an entry that contains the run ID, the "auto-fallback" label, and the file name.
    /// The date is determined at call time (DateTime.UtcNow) so the assertion checks
    /// the deterministic parts only.
    /// </summary>
    [Fact]
    public async Task AppendFallbackLogEntryAsync_NewLogMd_CreatesFileWithExpectedContent()
    {
        var runId = new RunId("run-xyz");

        await _sut.AppendFallbackLogEntryAsync(
            _tempDir, runId, new[] { "f.md" }, CancellationToken.None);

        var logPath = Path.Combine(_tempDir, "log.md");
        File.Exists(logPath).Should().BeTrue("AppendFallbackLogEntryAsync must create log.md");

        var content = await File.ReadAllTextAsync(logPath);
        content.Should().Contain("run-xyz");
        content.Should().Contain("auto-fallback");
        content.Should().Contain("f.md");
        // Verify the pipe-delimited structure so field reordering or separator changes are caught.
        content.Should().Contain("| run-xyz | auto-fallback |");
    }

    /// <summary>
    /// Calling AppendFallbackLogEntryAsync twice appends both entries in order —
    /// verifying that File.AppendAllTextAsync is used (not WriteAllTextAsync).
    /// </summary>
    [Fact]
    public async Task AppendFallbackLogEntryAsync_CalledTwice_BothEntriesAppearInOrder()
    {
        var firstRunId = new RunId("run-first");
        var secondRunId = new RunId("run-second");

        await _sut.AppendFallbackLogEntryAsync(
            _tempDir, firstRunId, new[] { "a.md" }, CancellationToken.None);
        await _sut.AppendFallbackLogEntryAsync(
            _tempDir, secondRunId, new[] { "b.md" }, CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDir, "log.md"));
        content.Should().Contain("run-first");
        content.Should().Contain("run-second");

        var firstIndex = content.IndexOf("run-first", StringComparison.Ordinal);
        var secondIndex = content.IndexOf("run-second", StringComparison.Ordinal);
        firstIndex.Should().BeLessThan(secondIndex,
            "the first call's entry must precede the second call's entry in log.md");
    }
}
