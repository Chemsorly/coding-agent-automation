using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;

namespace CodingAgentWebUI.Pipeline.UnitTests.CodeReview;

/// <summary>
/// Unit tests for <see cref="DiffHunkParser.ParseValidLines"/>.
/// DiffHunkParser is a pure static parser — all tests are in-memory with no I/O.
/// Verifies that the parser correctly identifies which lines in a unified diff are
/// valid targets for inline review comments (added lines only, not context or deleted).
/// </summary>
public sealed class DiffHunkParserTests
{
    // ── Null / empty input ────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_NullInput_ReturnsEmptyDictionary()
    {
        var result = DiffHunkParser.ParseValidLines(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseValidLines_EmptyInput_ReturnsEmptyDictionary()
    {
        var result = DiffHunkParser.ParseValidLines(string.Empty);
        result.Should().BeEmpty();
    }

    // ── Single added line ─────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_SingleAddedLine_ReturnsCorrectLineNumber()
    {
        // @@ -1,0 +1,1 @@ means the new file starts at line 1
        var diff = BuildDiff("new-file.cs",
            "@@ -0,0 +1,1 @@",
            "+added line");

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("new-file.cs");
        result["new-file.cs"].Should().Contain(1);
    }

    // ── Context lines are NOT valid targets ───────────────────────────────

    [Fact]
    public void ParseValidLines_ContextLine_IsNotValidTarget()
    {
        var diff = BuildDiff("file.cs",
            "@@ -1,3 +1,3 @@",
            " context line 1",  // space prefix = context
            "+added line",
            " context line 3");

        var result = DiffHunkParser.ParseValidLines(diff);

        result["file.cs"].Should().Contain(2, "added line at new-file line 2 is valid");
        result["file.cs"].Should().NotContain(1, "context line 1 is not a valid comment target");
        result["file.cs"].Should().NotContain(3, "context line 3 is not a valid comment target");
    }

    // ── Deleted lines do NOT advance the new-file line counter ───────────

    [Fact]
    public void ParseValidLines_DeletedLine_DoesNotAdvanceCounter()
    {
        // -1,2 +1,2: old had 2 lines, new has 2 lines
        // line 1 deleted, line 1 added => new-file line 1 is the added line
        var diff = BuildDiff("file.cs",
            "@@ -1,2 +1,2 @@",
            "-deleted line",  // does not advance new counter
            "+added line",    // new-file line 1
            " context");      // new-file line 2

        var result = DiffHunkParser.ParseValidLines(diff);

        result["file.cs"].Should().Contain(1, "added line is at new-file line 1");
        result["file.cs"].Should().NotContain(2); // context — not valid
    }

    // ── Multiple hunks in one file ────────────────────────────────────────

    [Fact]
    public void ParseValidLines_MultipleHunks_AllAddedLinesCollected()
    {
        var diff = BuildDiff("file.cs",
            "@@ -1,3 +1,3 @@",
            " context",
            "+first hunk addition",
            " context",
            "@@ -10,3 +10,3 @@",
            " context",
            "+second hunk addition",
            " context");

        var result = DiffHunkParser.ParseValidLines(diff);

        result["file.cs"].Should().Contain(2, "line 2 added in first hunk");
        result["file.cs"].Should().Contain(11, "line 11 added in second hunk");
    }

    // ── Multiple files ────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_MultipleFiles_EachMappedSeparately()
    {
        var diff =
            "diff --git a/file1.cs b/file1.cs\n" +
            "--- a/file1.cs\n" +
            "+++ b/file1.cs\n" +
            "@@ -1,1 +1,1 @@\n" +
            "+added in file1\n" +
            "diff --git a/file2.cs b/file2.cs\n" +
            "--- a/file2.cs\n" +
            "+++ b/file2.cs\n" +
            "@@ -1,1 +1,1 @@\n" +
            "+added in file2\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("file1.cs");
        result.Should().ContainKey("file2.cs");
        result["file1.cs"].Should().Contain(1);
        result["file2.cs"].Should().Contain(1);
    }

    // ── Deleted file (+++ /dev/null) ──────────────────────────────────────

    [Fact]
    public void ParseValidLines_DeletedFile_NoEntriesForDevNull()
    {
        var diff =
            "diff --git a/old.cs b/old.cs\n" +
            "--- a/old.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1,3 +0,0 @@\n" +
            "-line1\n" +
            "-line2\n" +
            "-line3\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        // Deleted file has no RIGHT-side lines — must not be keyed as "/dev/null"
        result.Should().NotContainKey("/dev/null");
    }

    // ── Line counter accuracy across hunk offsets ─────────────────────────

    [Fact]
    public void ParseValidLines_HunkStartsAtHighLineNumber_CorrectLineNumbers()
    {
        // @@ -100,4 +100,4 @@ — hunk starts at line 100 in new file
        var diff = BuildDiff("big-file.cs",
            "@@ -100,4 +100,4 @@",
            " context",        // new-file line 100
            "+added here",     // new-file line 101
            " context",        // new-file line 102
            "+also added");    // new-file line 103

        var result = DiffHunkParser.ParseValidLines(diff);

        result["big-file.cs"].Should().Contain(101);
        result["big-file.cs"].Should().Contain(103);
        result["big-file.cs"].Should().NotContain(100);
        result["big-file.cs"].Should().NotContain(102);
    }

    // ── Windows line endings (CRLF) ───────────────────────────────────────

    [Fact]
    public void ParseValidLines_CrlfLineEndings_ParsedCorrectly()
    {
        var diff =
            "diff --git a/file.cs b/file.cs\r\n" +
            "--- a/file.cs\r\n" +
            "+++ b/file.cs\r\n" +
            "@@ -1,1 +1,1 @@\r\n" +
            "+added line\r\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("file.cs");
        result["file.cs"].Should().Contain(1);
    }

    // ── File path normalisation ───────────────────────────────────────────

    [Fact]
    public void ParseValidLines_FilePathWithSubdirectory_KeyIsNormalisedPath()
    {
        var diff =
            "--- a/src/Services/MyService.cs\n" +
            "+++ b/src/Services/MyService.cs\n" +
            "@@ -1,1 +1,1 @@\n" +
            "+new line\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Services/MyService.cs");
    }

    // ── No hunk header means no valid lines ──────────────────────────────

    [Fact]
    public void ParseValidLines_FileWithNoHunkHeader_NoValidLines()
    {
        var diff =
            "--- a/file.cs\n" +
            "+++ b/file.cs\n" +
            "+this line has no hunk header above it\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        // File key exists (from the +++ line) but has no valid lines (no hunk header seen)
        if (result.ContainsKey("file.cs"))
            result["file.cs"].Should().BeEmpty("no hunk header means no valid lines");
    }

    // ── No-newline-at-end-of-file marker ─────────────────────────────────

    [Fact]
    public void ParseValidLines_NoNewlineMarker_DoesNotAdvanceCounter()
    {
        var diff = BuildDiff("file.cs",
            "@@ -1,1 +1,2 @@",
            "+line one",
            @"\ No newline at end of file",  // this marker must NOT be counted as a line
            "+line two");

        var result = DiffHunkParser.ParseValidLines(diff);

        // line one = 1, "\" marker skipped, line two = 2
        result["file.cs"].Should().Contain(1);
        result["file.cs"].Should().Contain(2);
    }

    // ── Crash-freedom ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("random text with no diff format")]
    [InlineData("@@ -1,1 +1,1 @@\n+line")]
    [InlineData("+++ b/file.cs\n@@ malformed hunk @@\n+line")]
    public void ParseValidLines_VariousInputs_NeverThrows(string input)
    {
        var act = () => DiffHunkParser.ParseValidLines(input);
        act.Should().NotThrow();
    }

    // ── Helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal unified diff string for a single file with the given hunk lines.
    /// </summary>
    private static string BuildDiff(string fileName, params string[] hunkLines)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"diff --git a/{fileName} b/{fileName}");
        sb.AppendLine($"--- a/{fileName}");
        sb.AppendLine($"+++ b/{fileName}");
        foreach (var line in hunkLines)
            sb.AppendLine(line);
        return sb.ToString();
    }
}
